using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BioSPPy.Net.Signals.Tools;
using BioSPPy.Net.Synthesizers.Ecg;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using DomainLanguage = CardioSimulator.Core.Domain.Language;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Hosts the Win2D <see cref="EcgMonitorControl"/> bound to the shared
/// <see cref="MonitorViewModel"/> (display mode) and <see cref="RhythmViewModel"/>
/// (waveforms). Adds zoom (mouse wheel, 1–5×) and pan (drag) over the monitor, mirroring
/// the Android <c>Monitor</c> pinch/graphicsLayer transform; the zoom level is persisted
/// back to the view-model after a short debounce and stays in sync with the scale dropdown.
/// </summary>
public sealed class MonitorView : Grid
{
    private readonly EcgMonitorControl _monitor = new();
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private DispatcherQueueTimer? _persistTimer;

    private float _scale = 1f;
    private float _lastModeScale = 1f;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private Point _lastPointer;
    private Point _pressPosition;

    // Ruler/caliper state. While active, a left-drag places two measurement points (instead of
    // panning) and zoom is suppressed so the measured interval stays meaningful.
    private bool _rulerActive;
    private bool _caliperDragging;
    private Point _caliperA;
    private Point _caliperB;

    private DomainLanguage _displayLanguage = DomainLanguage.EN;
    private bool _lastCompareMode;
    private readonly Dictionary<int, ComparisonTarget> _loadedTargets = new();

    /// <summary>Raised when a pane is tapped in compare mode, carrying the pane index.</summary>
    public event EventHandler<int>? PaneTapped;

    /// <summary>Language used to render compare-mode pane labels (pathology names).</summary>
    public DomainLanguage DisplayLanguage
    {
        get => _displayLanguage;
        set { _displayLanguage = value; SyncComparison(); }
    }

    public MonitorView()
    {
        Background = new SolidColorBrush(Colors.Transparent);

        // Zoom/pan are applied inside the monitor's Win2D draw (see EcgMonitorControl.SetView),
        // not via a XAML transform, so the trace stays crisp and line thickness stays constant.
        Children.Add(_monitor);

        SizeChanged += (_, _) => UpdateClip();
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => { _caliperDragging = false; EndDrag(e); };
        PointerCaptureLost += (_, _) => { _dragging = false; _caliperDragging = false; };
    }

    public EcgMonitorControl Monitor => _monitor;

    /// <summary>
    /// Toggles the ruler/caliper tool. While active, dragging on the monitor measures a time
    /// interval (ms) + rate (bpm) and amplitude (mV) instead of panning. Disabled in compare mode.
    /// </summary>
    public bool RulerActive
    {
        get => _rulerActive;
        set
        {
            if (_rulerActive == value) return;
            _rulerActive = value;
            _caliperDragging = false;
            _monitor.SetRulerActive(value);
        }
    }

    public void Bind(MonitorViewModel monitorVm, RhythmViewModel rhythmVm)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _monitor.Mode = monitorVm.MonitorMode;
        UpdateWaveforms();
        _monitor.SignificantPoints = rhythmVm.SignificantPoints;
        UpdateComparisonWaveforms();
        _scale = monitorVm.MonitorMode.Scale;
        _lastModeScale = _scale;
        _lastCompareMode = monitorVm.MonitorMode.IsCompareMode;
        ApplyTransform();
        monitorVm.PropertyChanged += OnMonitorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        SyncComparison();
    }

    private bool IsCompare => _monitorVm?.MonitorMode.IsCompareMode == true;

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorViewModel.MonitorMode) || _monitorVm is null) return;
        var mode = _monitorVm.MonitorMode;
        _monitor.Mode = mode;

        UpdateWaveforms();
        UpdateComparisonWaveforms();

        // Only treat a *scale* change (e.g. the scale dropdown) as external — count/scheme
        // changes leave mode.Scale untouched and must not reset the user's zoom.
        if (Math.Abs(mode.Scale - _lastModeScale) > 0.001f)
        {
            _lastModeScale = mode.Scale;
            if (Math.Abs(_scale - mode.Scale) > 0.001f)
            {
                _scale = mode.Scale;
                _offsetX = 0;
                _offsetY = 0;
                ApplyTransform();
            }
        }

        // Entering compare mode resets the zoom/pan so pane hit-testing maps 1:1 to pointer coords.
        if (mode.IsCompareMode && !_lastCompareMode)
        {
            _scale = 1f;
            _lastModeScale = 1f;
            _offsetX = 0;
            _offsetY = 0;
            ApplyTransform();
        }
        _lastCompareMode = mode.IsCompareMode;

        SyncComparison();
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Waveforms))
        {
            UpdateWaveforms();
        }
        else if (e.PropertyName == nameof(RhythmViewModel.SignificantPoints))
        {
            _monitor.SignificantPoints = _rhythmVm.SignificantPoints;
        }
        else if (e.PropertyName == nameof(RhythmViewModel.ComparisonWaveforms))
        {
            UpdateComparisonWaveforms();
        }
        else if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
        {
            SyncComparison(); // pane labels depend on the rhythm list
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerActive) return; // ruler measurements are screen-anchored; freeze zoom while active
        if (IsCompare) return; // no zoom in compare mode
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.1f : 1f / 1.1f;
        _scale = Math.Clamp(_scale * factor, 1f, 5f);
        ClampOffset();
        ApplyTransform();
        SchedulePersist();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        if (_rulerActive && !IsCompare)
        {
            _caliperA = pos;
            _caliperB = pos;
            _caliperDragging = true;
            PushCalipers();
            CapturePointer(e.Pointer);
            return;
        }

        _pressPosition = pos;
        _lastPointer = _pressPosition;
        _dragging = !IsCompare; // compare mode: taps only, no pan
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_caliperDragging)
        {
            _caliperB = e.GetCurrentPoint(this).Position;
            PushCalipers();
            return;
        }
        if (!_dragging) return;
        var p = e.GetCurrentPoint(this).Position;
        _offsetX += p.X - _lastPointer.X;
        _offsetY += p.Y - _lastPointer.Y;
        _lastPointer = p;
        ClampOffset();
        ApplyTransform();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_caliperDragging)
        {
            _caliperDragging = false;
            // A bare click (no drag) clears the measurement.
            if (Math.Abs(_caliperB.X - _caliperA.X) + Math.Abs(_caliperB.Y - _caliperA.Y) < 4)
                _monitor.SetCalipers(null, null);
            ReleasePointerCapture(e.Pointer);
            return;
        }
        if (IsCompare)
        {
            var p = e.GetCurrentPoint(this).Position;
            var moved = Math.Abs(p.X - _pressPosition.X) + Math.Abs(p.Y - _pressPosition.Y);
            if (moved < 8)
            {
                var pane = _monitor.PaneIndexAt(p.X, p.Y);
                if (pane >= 0) PaneTapped?.Invoke(this, pane);
            }
        }
        EndDrag(e);
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void ClampOffset()
    {
        var maxX = ActualWidth * (_scale - 1) / 2;
        var maxY = ActualHeight * (_scale - 1) / 2;
        _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
        _offsetY = Math.Clamp(_offsetY, -maxY, maxY);
    }

    private void ApplyTransform() =>
        _monitor.SetView(_scale, (float)_offsetX, (float)_offsetY);

    private void PushCalipers() => _monitor.SetCalipers(_caliperA, _caliperB);

    private void UpdateClip() =>
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };

    private void SchedulePersist()
    {
        if (_persistTimer is null)
        {
            _persistTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _persistTimer.IsRepeating = false;
            _persistTimer.Interval = TimeSpan.FromMilliseconds(500);
            _persistTimer.Tick += OnPersistTick;
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void OnPersistTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _lastModeScale = _scale;
        _monitorVm?.SetScale(_scale);
    }

    /// <summary>
    /// Reconciles compare-mode targets with loaded waveforms (load new/changed, drop removed)
    /// and recomputes the per-pane display labels. Mirrors the Android
    /// <c>LaunchedEffect(comparisonTargets)</c> reactive load.
    /// </summary>
    private void SyncComparison()
    {
        if (_monitorVm is null || _rhythmVm is null) return;
        var mode = _monitorVm.MonitorMode;

        if (!mode.IsCompareMode)
        {
            if (_loadedTargets.Count > 0)
            {
                _loadedTargets.Clear();
                _rhythmVm.ClearComparisonWaveforms();
            }
            _monitor.ComparisonLabels = new Dictionary<int, string>();
            return;
        }

        // Load waveforms for new or changed targets.
        foreach (var (pane, target) in mode.ComparisonTargets)
        {
            if (!_loadedTargets.TryGetValue(pane, out var prev) || prev != target)
            {
                _loadedTargets[pane] = target;
                _ = _rhythmVm.LoadComparisonWaveformAsync(pane, target.PathologyId, target.Lead);
            }
        }

        // Drop waveforms for panes whose target was removed.
        foreach (var pane in _loadedTargets.Keys.ToList())
        {
            if (!mode.ComparisonTargets.ContainsKey(pane))
            {
                _loadedTargets.Remove(pane);
                _rhythmVm.RemoveComparisonWaveform(pane);
            }
        }

        // Compute display labels (pathology name in the active language).
        var labels = new Dictionary<int, string>();
        foreach (var (pane, target) in mode.ComparisonTargets)
        {
            var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == target.PathologyId);
            labels[pane] = entry is null
                ? target.PathologyId
                : (_displayLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn);
        }
        _monitor.ComparisonLabels = labels;
    }

    private void UpdateWaveforms()
    {
        if (_rhythmVm is null || _monitorVm is null) return;
        var rawMap = _rhythmVm.Waveforms;
        var mode = _monitorVm.MonitorMode;

        // An electrode-hookup fault is a wiring error at the source, so the lead remap (RA/LA
        // reversal, or attenuated precordial leads) is applied to the whole lead set before the
        // per-lead recording artifacts and cleanup filter below.
        var sourceMap = ElectrodeFault.Apply(rawMap, mode.ElectrodeState);

        IReadOnlyDictionary<Lead, Points> processed = sourceMap;
        if (sourceMap.Count > 0 && (mode.Artifacts != EcgArtifacts.None || mode.FilterType != EcgFilterType.None))
        {
            double fs = SampleRate(mode);
            var filter = mode.FilterType != EcgFilterType.None ? BuildFilter(mode.FilterType, fs) : null;
            var map = new Dictionary<Lead, Points>(sourceMap.Count);
            foreach (var kvp in sourceMap)
            {
                // Recording artifacts represent on-the-wire noise, so add them before the cleanup
                // filter — a student can overlay mains hum and watch a low-pass filter remove it.
                var pts = kvp.Value;
                if (mode.Artifacts != EcgArtifacts.None)
                    pts = AddArtifacts(pts, mode.Artifacts, fs, seedBase: (int)kvp.Key * 31);
                if (filter is { } f)
                    pts = ApplyFilter(pts, f.b, f.a);
                map[kvp.Key] = pts;
            }
            processed = map;
        }

        _monitor.Waveforms = processed;
        UpdateSqi(processed);
    }

    private void UpdateComparisonWaveforms()
    {
        if (_rhythmVm is null || _monitorVm is null) return;
        var rawMap = _rhythmVm.ComparisonWaveforms;
        var mode = _monitorVm.MonitorMode;

        IReadOnlyDictionary<int, Points> processed = rawMap;
        if (rawMap.Count > 0 && (mode.Artifacts != EcgArtifacts.None || mode.FilterType != EcgFilterType.None))
        {
            double fs = SampleRate(mode);
            var filter = mode.FilterType != EcgFilterType.None ? BuildFilter(mode.FilterType, fs) : null;
            var map = new Dictionary<int, Points>(rawMap.Count);
            foreach (var kvp in rawMap)
            {
                var pts = kvp.Value;
                if (mode.Artifacts != EcgArtifacts.None)
                    pts = AddArtifacts(pts, mode.Artifacts, fs, seedBase: kvp.Key * 31 + 7);
                if (filter is { } f)
                    pts = ApplyFilter(pts, f.b, f.a);
                map[kvp.Key] = pts;
            }
            processed = map;
        }

        _monitor.ComparisonWaveforms = processed;
    }

    // ── Signal processing helpers (artifacts + filter), shared by both maps ──────

    private static double SampleRate(MonitorModeModel mode)
        => mode.Calibration is { SampleRateHz: > 0 } cal ? cal.SampleRateHz : 1000.0;

    /// <summary>Builds Butterworth coefficients for the chosen filter band, or null on failure.</summary>
    private static (double[] b, double[] a)? BuildFilter(EcgFilterType filterType, double fs)
    {
        double nyq = fs / 2.0;
        try
        {
            return filterType switch
            {
                EcgFilterType.Lowpass => Filtering.Butterworth(2, new[] { 40.0 / nyq }, "lowpass"),
                EcgFilterType.Highpass => Filtering.Butterworth(2, new[] { 0.5 / nyq }, "highpass"),
                EcgFilterType.Bandpass => Filtering.Butterworth(2, new[] { 0.5 / nyq, 40.0 / nyq }, "bandpass"),
                _ => ((double[] b, double[] a)?)null,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filter build failed: {ex.Message}");
            return null;
        }
    }

    private static Points ApplyFilter(Points points, double[] b, double[] a)
    {
        var vals = points.Values;
        if (vals.Count < 15) return points; // too short for filtfilt padding
        try
        {
            double[] sig = vals.Select(x => (double)x).ToArray();
            double[] filt = Filtering.FiltFilt(b, a, sig);
            return new Points(filt.Select(x => (float)x).ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filtering failed: {ex.Message}");
            return points;
        }
    }

    /// <summary>
    /// Sums the noise of every active artifact onto the lead's samples. Each artifact is scaled to the
    /// clean signal's peak-to-peak range and seeded deterministically (per lead + kind) so the trace
    /// looks alive yet stays stable across re-renders (zoom, scale, …).
    /// </summary>
    private static Points AddArtifacts(Points points, EcgArtifacts artifacts, double fs, int seedBase)
    {
        var vals = points.Values;
        int n = vals.Count;
        if (n == 0) return points;

        double[] sig = vals.Select(x => (double)x).ToArray();
        double reference = PeakToPeak(sig);
        int seed = seedBase;
        try
        {
            foreach (var kind in ActiveKinds(artifacts))
            {
                double[] noise = EcgArtifactGenerator.Generate(n, kind, fs, reference, intensity: 1.0, seed: seed++);
                for (int i = 0; i < n; i++) sig[i] += noise[i];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Artifact generation failed: {ex.Message}");
            return points;
        }
        return new Points(sig.Select(x => (float)x).ToArray());
    }

    private static IEnumerable<EcgArtifactKind> ActiveKinds(EcgArtifacts artifacts)
    {
        if (artifacts.HasFlag(EcgArtifacts.Muscle)) yield return EcgArtifactKind.Muscle;
        if (artifacts.HasFlag(EcgArtifacts.Mains)) yield return EcgArtifactKind.Mains;
        if (artifacts.HasFlag(EcgArtifacts.Baseline)) yield return EcgArtifactKind.Baseline;
        if (artifacts.HasFlag(EcgArtifacts.Contact)) yield return EcgArtifactKind.Contact;
        if (artifacts.HasFlag(EcgArtifacts.Motion)) yield return EcgArtifactKind.Motion;
    }

    private static double PeakToPeak(double[] signal)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < signal.Length; i++)
        {
            if (signal[i] < min) min = signal[i];
            if (signal[i] > max) max = signal[i];
        }
        return signal.Length == 0 ? 0.0 : max - min;
    }

    // Computes the SQI of the displayed (filtered) trace and pushes it to the view-model, where the
    // monitor's Filters dropdown surfaces it. (Previously drawn as a card overlaid on the monitor.)
    private void UpdateSqi(IReadOnlyDictionary<Lead, Points> map)
    {
        if (_monitorVm is null) return;

        if (map == null || map.Count == 0)
        {
            _monitorVm.SetSignalQuality(null);
            return;
        }

        var primaryLead = map.Keys.Contains(Lead.II) ? Lead.II : map.Keys.First();
        var vals = map[primaryLead].Values;

        if (vals.Count < 100)
        {
            _monitorVm.SetSignalQuality(null);
            return;
        }

        double[] signalDouble = vals.Select(x => (double)x).ToArray();
        double fs = 1000.0;
        if (_monitorVm.MonitorMode.Calibration is { } cal && cal.SampleRateHz > 0)
        {
            fs = cal.SampleRateHz;
        }

        double ssqi = BioSPPy.Net.Signals.Ecg.Sqi.SSQI(signalDouble);
        double ksqi = BioSPPy.Net.Signals.Ecg.Sqi.KSQI(signalDouble);
        double psqi = BioSPPy.Net.Signals.Ecg.Sqi.PSQI(signalDouble);

        int[] detector1 = BioSPPy.Net.Signals.Ecg.QrsSegmenters.HamiltonSegmenter(signalDouble, fs);
        int[] detector2 = BioSPPy.Net.Signals.Ecg.QrsSegmenters.SsfSegmenter(signalDouble, fs);
        string quality = BioSPPy.Net.Signals.Ecg.Sqi.ZZ2018(signalDouble, detector1, detector2, fs, mode: "fuzzy");

        _monitorVm.SetSignalQuality(new SignalQualityInfo(quality, ssqi, ksqi, psqi, primaryLead));
    }
}
