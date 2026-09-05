using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace CardioSimulator.App.Audio;

/// <summary>
/// Plays the classic bedside-monitor "beep" — a short sine blip synthesised once into an in-memory
/// WAV and replayed through a WinRT <see cref="MediaPlayer"/>. (<c>System.Media.SoundPlayer</c> lives
/// in the Windows Desktop pack, which an unpackaged WinUI app doesn't reference, so we stay on the
/// WinRT audio stack and ship no audio asset.) One instance drives the R-peak pulse tone for a single
/// monitor: call <see cref="Beep"/> on every detected R-peak. Audio init is async and best-effort — if
/// there is no output device (or the codec fails) the object degrades to a silent no-op.
/// </summary>
public sealed class MonitorBeeper : IDisposable
{
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _stream;
    private volatile bool _ready;
    private volatile bool _disposed;

    public MonitorBeeper()
    {
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var wav = BuildBeepWav();
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(wav.AsBuffer());
            stream.Seek(0);

            var player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = 0.7,
                Source = MediaSource.CreateFromStream(stream, "audio/wav"),
            };

            // Disposed while we were awaiting — don't leak the freshly built engine.
            if (_disposed)
            {
                player.Dispose();
                stream.Dispose();
                return;
            }

            _stream = stream;
            _player = player;
            _ready = true;
        }
        catch
        {
            // No audio device / codec — beeps become no-ops rather than crashing the monitor.
            _ready = false;
        }
    }

    /// <summary>Replays the beep from its start. No-op until async init has completed (or if it failed).</summary>
    public void Beep()
    {
        if (!_ready || _player is null) return;
        try
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch
        {
            // Ignore transient playback races (e.g. Play() while the engine is re-seeking).
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _ready = false;
        try { _player?.Dispose(); } catch { /* best-effort */ }
        try { _stream?.Dispose(); } catch { /* best-effort */ }
        _player = null;
        _stream = null;
    }

    // ── WAV synthesis ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a mono 16-bit PCM WAV holding one short beep: an 880 Hz sine with a 4 ms fade-in and an
    /// exponential decay (plus a short linear tail), so it reads as a crisp monitor blip with no click.
    /// </summary>
    private static byte[] BuildBeepWav(
        int sampleRate = 44100, double freqHz = 880.0, double durationSec = 0.085, double gain = 0.6)
    {
        var n = Math.Max(1, (int)(sampleRate * durationSec));
        var pcm = new short[n];
        var attack = Math.Max(1, (int)(sampleRate * 0.004));   // 4 ms fade-in (kills the onset click)
        var release = Math.Max(1, (int)(sampleRate * 0.010));  // 10 ms linear fade-out (clean tail)
        var w = 2.0 * Math.PI * freqHz / sampleRate;
        for (var i = 0; i < n; i++)
        {
            double env;
            if (i < attack) env = (double)i / attack;
            else env = Math.Exp(-3.5 * (i - attack) / Math.Max(1, n - attack));
            if (i > n - release) env *= (double)(n - i) / release;
            var s = Math.Sin(w * i) * env * gain;
            pcm[i] = (short)(Math.Clamp(s, -1.0, 1.0) * short.MaxValue);
        }
        return WrapWav(pcm, sampleRate);
    }

    private static byte[] WrapWav(short[] samples, int sampleRate)
    {
        const int channels = 1, bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataBytes = samples.Length * 2;

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                    // PCM fmt chunk size
        w.Write((short)1);              // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
