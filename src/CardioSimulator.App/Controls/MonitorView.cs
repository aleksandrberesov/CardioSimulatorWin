using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BioSPPy.Net.Signals.Tools;
using BioSPPy.Net.Synthesizers.Ecg;
using CardioSimulator.App.Localization;
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
using Windows.UI;

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

    // Translucent measurements readout (the "values column"), pinned top-right. Shown when the
    // pQRSt toggle (ShowImpulseLabels) is on and the active rhythm has significant-point markup;
    // its header checkbox flips the on-trace annotations (ShowImpulseGraphOverlay).
    private Border? _measurementsCard;
    private TextBlock? _measurementsTitle;
    private StackPanel? _measurementsRows;
    private CheckBox? _onGraphCheck;
    private bool _suppressOnGraphEvent;
    private static readonly Color CardFill = Color.FromArgb(0xCC, 0x14, 0x1C, 0x18);
    private static readonly Color CardBorder = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
    private static readonly Color CardDivider = Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF);
    private static readonly Color CardLabel = Color.FromArgb(0xFF, 0xCF, 0xE8, 0xDC);

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
        BuildMeasurementsCard();

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
        RefreshMeasurements();
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
        RefreshMeasurements();
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
            RefreshMeasurements();
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

    // ── Measurements readout ("values column") ──────────────────────────────────

    /// <summary>
    /// Builds the translucent measurements card once and layers it over the monitor (top-right).
    /// It stays collapsed until <see cref="RefreshMeasurements"/> shows it. The card swallows pointer
    /// presses so tapping it doesn't start a pan/caliper on the monitor underneath.
    /// </summary>
    private void BuildMeasurementsCard()
    {
        _measurementsTitle = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
        };
        _onGraphCheck = new CheckBox
        {
            FontSize = 12,
            MinWidth = 0,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, -4, 0, -4),
        };
        _onGraphCheck.Checked += OnGraphCheckToggled;
        _onGraphCheck.Unchecked += OnGraphCheckToggled;

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_measurementsTitle);
        header.Children.Add(_onGraphCheck);

        _measurementsRows = new StackPanel { Spacing = 2 };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(header);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(CardDivider),
            Margin = new Thickness(0, 4, 0, 2),
        });
        content.Children.Add(_measurementsRows);

        _measurementsCard = new Border
        {
            Background = new SolidColorBrush(CardFill),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            MinWidth = 136,
            Visibility = Visibility.Collapsed,
            Child = content,
        };
        _measurementsCard.PointerPressed += (_, e) => e.Handled = true;
        Children.Add(_measurementsCard);
    }

    /// <summary>
    /// Recomputes the measurements column from the active rhythm's significant points and shows it
    /// when the pQRSt readout is on (single-rhythm mode only). Also syncs the "On graph" checkbox to
    /// <see cref="MonitorModeModel.ShowImpulseGraphOverlay"/> without re-raising its toggle.
    /// </summary>
    private void RefreshMeasurements()
    {
        if (_measurementsCard is null || _measurementsRows is null || _onGraphCheck is null
            || _measurementsTitle is null || _monitorVm is null || _rhythmVm is null) return;

        var mode = _monitorVm.MonitorMode;
        if (!mode.ShowImpulseLabels || mode.IsCompareMode)
        {
            _measurementsCard.Visibility = Visibility.Collapsed;
            return;
        }

        var fs = mode.Calibration is { SampleRateHz: > 0 } cal ? cal.SampleRateHz : 0.0;
        var set = EcgMeasurements.Compute(_rhythmVm.SignificantPoints, fs);
        if (!set.HasAny)
        {
            _measurementsCard.Visibility = Visibility.Collapsed;
            return;
        }

        // Labels follow the active language (the card is built once, so re-apply on every refresh).
        _measurementsTitle.Text = AppStrings.MonitorMeasurementsTitle;
        _onGraphCheck.Content = AppStrings.MonitorMeasurementsOnGraph;

        _suppressOnGraphEvent = true;
        _onGraphCheck.IsChecked = mode.ShowImpulseGraphOverlay;
        _suppressOnGraphEvent = false;

        _measurementsRows.Children.Clear();
        if (set.HeartRateBpm is { } hr) AddMeasurementRow(AppStrings.MonitorHrLabel, AppStrings.MonitorHrValueFormat(hr));
        if (set.RrSeconds is { } rr) AddMeasurementRow(AppStrings.EcgIntervalRr, AppStrings.EcgSecondsValueFormat(rr));
        if (set.PSeconds is { } p) AddMeasurementRow(AppStrings.EcgIntervalP, AppStrings.EcgSecondsValueFormat(p));
        if (set.PrSeconds is { } pr) AddMeasurementRow(AppStrings.EcgIntervalPr, AppStrings.EcgSecondsValueFormat(pr));
        if (set.QrsSeconds is { } qrs) AddMeasurementRow(AppStrings.EcgIntervalQrs, AppStrings.EcgSecondsValueFormat(qrs));
        if (set.QtSeconds is { } qt) AddMeasurementRow(AppStrings.EcgIntervalQt, AppStrings.EcgSecondsValueFormat(qt));
        if (set.StSeconds is { } st) AddMeasurementRow(AppStrings.EcgIntervalSt, AppStrings.EcgSecondsValueFormat(st));
        if (set.TSeconds is { } t) AddMeasurementRow(AppStrings.EcgIntervalT, AppStrings.EcgSecondsValueFormat(t));

        _measurementsCard.Visibility = Visibility.Visible;
    }

    // One "label ⋯ value" row: wave label left, monospaced value right-aligned.
    private void AddMeasurementRow(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(CardLabel) };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        _measurementsRows!.Children.Add(grid);
    }

    private void OnGraphCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressOnGraphEvent || _monitorVm is null || _onGraphCheck is null) return;
        _monitorVm.SetShowImpulseGraphOverlay(_onGraphCheck.IsChecked == true);
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
