using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CardioSimulator.App.Audio;

/// <summary>
/// Drives the bedside-monitor pulse tone through a single continuous WASAPI shared-mode render stream.
/// <para>
/// Why WASAPI and not a one-shot (winmm PlaySound / WinRT MediaPlayer): on a Bluetooth-only machine the
/// A2DP link powers down between sounds, so an isolated short beep is dropped (only continuous audio like
/// video survives). A one-shot long tone works but can't be a per-heartbeat cue (it overlaps at speed).
/// This engine instead keeps ONE render stream permanently open, filling it with near-inaudible low-level
/// noise so the endpoint — and thus the Bluetooth link — never idles; the beep tone is then synthesised
/// straight into that same stream on demand, so short crisp beeps play immediately at any heart rate.
/// (MediaPlayer, tried for the same job, crashed this app; winmm PlaySound couldn't co-exist with a
/// keep-alive stream. Raw WASAPI is stable and self-contained — no shipped asset, no WinRT media pipeline.)
/// </para>
/// A background MTA thread owns the stream and re-initialises it if the device drops (the JBL flaps), so
/// the keep-alive survives reconnects. All state shared with the UI thread is guarded by <see cref="_gate"/>.
/// </summary>
public sealed class MonitorBeeper : IDisposable
{
    // Keep-alive signal: a continuous sub-bass tone. It must be NON-ZERO so Bluetooth won't suspend the
    // A2DP link (that's what makes short beeps play), but broadband noise was audible as a hiss — so we
    // use a 30 Hz tone instead: robustly non-zero on the wire, yet below what a small speaker can
    // reproduce, so it's inaudible. (Raise the level if beeps ever cut out; lower it / drop frequency if
    // any rumble is audible.)
    private const double KeepAliveFreqHz = 30.0;
    private const double KeepAliveLevel = 0.03;
    private const double BeepFreqHz = 880.0;
    private const double ShortBeepSec = 0.14;  // crisp R-peak blip
    private const double TestBeepSec = 0.40;   // Settings "Check sound"

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private double _volume;

    // Trigger hand-off (UI thread → render thread): a pending beep duration in seconds, -1 when none.
    private double _pendingBeepSec = -1;
    private double _pendingBeepAmp;

    public MonitorBeeper(double volume = 0.6)
    {
        _volume = Math.Clamp(volume, 0.0, 1.0);
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "MonitorBeeper" };
        try { _thread.SetApartmentState(ApartmentState.MTA); } catch { }
        _thread.Start();
    }

    public void SetVolume(double volume)
    {
        lock (_gate) _volume = Math.Clamp(volume, 0.0, 1.0);
    }

    /// <summary>Fires the short R-peak blip.</summary>
    public void Beep() => Trigger(ShortBeepSec);

    /// <summary>Fires the longer "Check sound" test tone.</summary>
    public void PlayTest() => Trigger(TestBeepSec);

    private void Trigger(double seconds)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pendingBeepSec = seconds;
            _pendingBeepAmp = _volume;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _stop = true;
        try { _thread?.Join(500); } catch { }
        _thread = null;
    }

    // ── Render thread ─────────────────────────────────────────────────────────

    // Re-init loop: keeps a stream open; if the device drops (JBL disconnects) the inner loop throws and
    // we retry after a pause, so the keep-alive re-establishes when the speaker comes back.
    private void RenderLoop()
    {
        while (!_stop)
        {
            try { RenderOnce(); }
            catch { /* device lost / init failed — retry below */ }
            if (_stop) break;
            // Device gone or init failed — wait, then retry (handles the flapping JBL / no-device-at-start).
            for (var i = 0; i < 20 && !_stop; i++) Thread.Sleep(50);
        }
    }

    private void RenderOnce()
    {
        object? enumObj = null, deviceObj = null, clientObj = null, renderObj = null;
        var fmtPtr = IntPtr.Zero;
        try
        {
            enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!);
            var enumerator = (IMMDeviceEnumerator)enumObj!;
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out deviceObj) != 0 || deviceObj is null)
                return;
            var device = (IMMDevice)deviceObj;

            var iidClient = IID_IAudioClient;
            if (device.Activate(ref iidClient, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out clientObj) != 0 || clientObj is null)
                return;
            var client = (IAudioClient)clientObj;

            if (client.GetMixFormat(out fmtPtr) != 0 || fmtPtr == IntPtr.Zero) return;
            var channels = Marshal.ReadInt16(fmtPtr, 2);
            var sampleRate = Marshal.ReadInt32(fmtPtr, 4);
            var bits = Marshal.ReadInt16(fmtPtr, 14);
            if (channels <= 0 || sampleRate <= 0 || (bits != 32 && bits != 16)) return;

            // 100 ms shared-mode buffer.
            if (client.Initialize(0 /*SHARED*/, 0, 1_000_000L, 0, fmtPtr, IntPtr.Zero) != 0) return;
            if (client.GetBufferSize(out var bufferFrames) != 0 || bufferFrames == 0) return;

            var iidRender = IID_IAudioRenderClient;
            if (client.GetService(ref iidRender, out renderObj) != 0 || renderObj is null) return;
            var render = (IAudioRenderClient)renderObj;

            if (client.Start() != 0) return;

            var kaInc = 2.0 * Math.PI * KeepAliveFreqHz / sampleRate; // keep-alive sub-bass phase step
            var kaPhase = 0.0;
            var beepRemaining = 0;      // render-thread-owned
            var beepTotal = 0;
            var beepPos = 0;
            var beepAmp = 0.0;
            var twoPiFOverSr = 2.0 * Math.PI * BeepFreqHz / sampleRate;
            var floatBuf = bits == 32 ? new float[bufferFrames * channels] : null;
            var shortBuf = bits == 16 ? new short[bufferFrames * channels] : null;

            while (!_stop)
            {
                // Pick up a pending trigger.
                lock (_gate)
                {
                    if (_pendingBeepSec > 0)
                    {
                        beepTotal = Math.Max(1, (int)(_pendingBeepSec * sampleRate));
                        beepRemaining = beepTotal;
                        beepPos = 0;
                        beepAmp = _pendingBeepAmp;
                        _pendingBeepSec = -1;
                    }
                }

                if (client.GetCurrentPadding(out var padding) != 0) throw new InvalidOperationException("padding");
                var framesToWrite = (int)bufferFrames - (int)padding;
                if (framesToWrite > 0)
                {
                    if (render.GetBuffer((uint)framesToWrite, out var buf) != 0 || buf == IntPtr.Zero)
                        throw new InvalidOperationException("getbuffer");

                    var attack = Math.Max(1, sampleRate / 250); // 4 ms
                    for (var f = 0; f < framesToWrite; f++)
                    {
                        double s = Math.Sin(kaPhase) * KeepAliveLevel;
                        kaPhase += kaInc;
                        if (kaPhase > 2.0 * Math.PI) kaPhase -= 2.0 * Math.PI;
                        if (beepRemaining > 0)
                        {
                            double env;
                            if (beepPos < attack) env = (double)beepPos / attack;
                            else env = Math.Exp(-3.0 * (beepPos - attack) / Math.Max(1, beepTotal - attack));
                            s += Math.Sin(twoPiFOverSr * beepPos) * env * beepAmp;
                            beepPos++;
                            beepRemaining--;
                        }
                        if (s > 1.0) s = 1.0; else if (s < -1.0) s = -1.0;
                        var baseIdx = f * channels;
                        if (floatBuf != null)
                            for (var c = 0; c < channels; c++) floatBuf[baseIdx + c] = (float)s;
                        else
                            for (var c = 0; c < channels; c++) shortBuf![baseIdx + c] = (short)(s * short.MaxValue);
                    }

                    if (floatBuf != null) Marshal.Copy(floatBuf, 0, buf, framesToWrite * channels);
                    else Marshal.Copy(shortBuf!, 0, buf, framesToWrite * channels);
                    render.ReleaseBuffer((uint)framesToWrite, 0);
                }
                Thread.Sleep(8);
            }

            try { client.Stop(); } catch { }
        }
        finally
        {
            if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
            if (renderObj != null) Marshal.ReleaseComObject(renderObj);
            if (clientObj != null) Marshal.ReleaseComObject(clientObj);
            if (deviceObj != null) Marshal.ReleaseComObject(deviceObj);
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
        }
    }

    // ── COM interop ───────────────────────────────────────────────────────────

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.IUnknown)] out object device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
    }
}
