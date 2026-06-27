using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();
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

    private DomainLanguage _displayLanguage = DomainLanguage.EN;
    private bool _lastCompareMode;
    private readonly Dictionary<int, ComparisonTarget> _loadedTargets = new();

    // SQI Widgets
    private readonly Border _sqiCard = new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 10, 6),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 0, 0)),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _sqiDot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sqiLabel = new() { Text = "Quality: -", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sqiDetails = new() { Text = "sSQI: - | kSQI: -", FontSize = 11, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) };

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

        var group = new TransformGroup();
        group.Children.Add(_scaleTransform);
        group.Children.Add(_translateTransform);
        _monitor.RenderTransform = group;
        _monitor.RenderTransformOrigin = new Point(0.5, 0.5);
        Children.Add(_monitor);

        SizeChanged += (_, _) => UpdateClip();
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => _dragging = false;

        // Setup SQI card layout
        var sqiPanel = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_sqiDot);
        titleRow.Children.Add(_sqiLabel);
        sqiPanel.Children.Add(titleRow);
        sqiPanel.Children.Add(_sqiDetails);
        _sqiCard.Child = sqiPanel;
        Children.Add(_sqiCard);
    }

    public EcgMonitorControl Monitor => _monitor;

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
        _pressPosition = e.GetCurrentPoint(this).Position;
        _lastPointer = _pressPosition;
        _dragging = !IsCompare; // compare mode: taps only, no pan
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
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

    private void ApplyTransform()
    {
        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translateTransform.X = _offsetX;
        _translateTransform.Y = _offsetY;
    }

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
        var filterType = _monitorVm.MonitorMode.FilterType;

        if (filterType == EcgFilterType.None || rawMap.Count == 0)
        {
            _monitor.Waveforms = rawMap;
            UpdateSqi(rawMap);
            return;
        }

        var filteredMap = new Dictionary<Lead, Points>();
        double fs = 1000.0;
        if (_monitorVm.MonitorMode.Calibration is { } cal && cal.SampleRateHz > 0)
        {
            fs = cal.SampleRateHz;
        }

        double[] b, a;
        try
        {
            double nyq = fs / 2.0;
            switch (filterType)
            {
                case EcgFilterType.Lowpass:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 40.0 / nyq }, band: "lowpass");
                    break;
                case EcgFilterType.Highpass:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 0.5 / nyq }, band: "highpass");
                    break;
                case EcgFilterType.Bandpass:
                default:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 0.5 / nyq, 40.0 / nyq }, band: "bandpass");
                    break;
            }

            foreach (var kvp in rawMap)
            {
                var originalVals = kvp.Value.Values;
                if (originalVals.Count < 15)
                {
                    filteredMap[kvp.Key] = kvp.Value;
                    continue;
                }
                double[] sigDouble = originalVals.Select(x => (double)x).ToArray();
                double[] filtDouble = BioSPPy.Net.Signals.Tools.Filtering.FiltFilt(b, a, sigDouble);
                float[] filtFloat = filtDouble.Select(x => (float)x).ToArray();
                filteredMap[kvp.Key] = new Points(filtFloat);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filtering failed: {ex.Message}");
            filteredMap = new Dictionary<Lead, Points>(rawMap);
        }

        _monitor.Waveforms = filteredMap;
        UpdateSqi(filteredMap);
    }

    private void UpdateComparisonWaveforms()
    {
        if (_rhythmVm is null || _monitorVm is null) return;
        var rawMap = _rhythmVm.ComparisonWaveforms;
        var filterType = _monitorVm.MonitorMode.FilterType;

        if (filterType == EcgFilterType.None || rawMap.Count == 0)
        {
            _monitor.ComparisonWaveforms = rawMap;
            return;
        }

        var filteredMap = new Dictionary<int, Points>();
        double fs = 1000.0;
        if (_monitorVm.MonitorMode.Calibration is { } cal && cal.SampleRateHz > 0)
        {
            fs = cal.SampleRateHz;
        }

        double[] b, a;
        try
        {
            double nyq = fs / 2.0;
            switch (filterType)
            {
                case EcgFilterType.Lowpass:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 40.0 / nyq }, band: "lowpass");
                    break;
                case EcgFilterType.Highpass:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 0.5 / nyq }, band: "highpass");
                    break;
                case EcgFilterType.Bandpass:
                default:
                    (b, a) = BioSPPy.Net.Signals.Tools.Filtering.Butterworth(order: 2, Wn: new double[] { 0.5 / nyq, 40.0 / nyq }, band: "bandpass");
                    break;
            }

            foreach (var kvp in rawMap)
            {
                var originalVals = kvp.Value.Values;
                if (originalVals.Count < 15)
                {
                    filteredMap[kvp.Key] = kvp.Value;
                    continue;
                }
                double[] sigDouble = originalVals.Select(x => (double)x).ToArray();
                double[] filtDouble = BioSPPy.Net.Signals.Tools.Filtering.FiltFilt(b, a, sigDouble);
                float[] filtFloat = filtDouble.Select(x => (float)x).ToArray();
                filteredMap[kvp.Key] = new Points(filtFloat);
            }
        }
        catch
        {
            filteredMap = new Dictionary<int, Points>(rawMap);
        }

        _monitor.ComparisonWaveforms = filteredMap;
    }

    private void UpdateSqi(IReadOnlyDictionary<Lead, Points> map)
    {
        if (map == null || map.Count == 0)
        {
            _sqiCard.Visibility = Visibility.Collapsed;
            return;
        }

        var primaryLead = map.Keys.Contains(Lead.II) ? Lead.II : map.Keys.First();
        var vals = map[primaryLead].Values;

        if (vals.Count < 100)
        {
            _sqiCard.Visibility = Visibility.Collapsed;
            return;
        }

        _sqiCard.Visibility = Visibility.Visible;

        double[] signalDouble = vals.Select(x => (double)x).ToArray();
        double fs = 1000.0;
        if (_monitorVm?.MonitorMode.Calibration is { } cal && cal.SampleRateHz > 0)
        {
            fs = cal.SampleRateHz;
        }

        double ssqi = BioSPPy.Net.Signals.Ecg.Sqi.SSQI(signalDouble);
        double ksqi = BioSPPy.Net.Signals.Ecg.Sqi.KSQI(signalDouble);
        double psqi = BioSPPy.Net.Signals.Ecg.Sqi.PSQI(signalDouble);

        int[] detector1 = BioSPPy.Net.Signals.Ecg.QrsSegmenters.HamiltonSegmenter(signalDouble, fs);
        int[] detector2 = BioSPPy.Net.Signals.Ecg.QrsSegmenters.SsfSegmenter(signalDouble, fs);
        string quality = BioSPPy.Net.Signals.Ecg.Sqi.ZZ2018(signalDouble, detector1, detector2, fs, mode: "fuzzy");

        var green = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
        var yellow = new SolidColorBrush(Microsoft.UI.Colors.Gold);
        var red = new SolidColorBrush(Microsoft.UI.Colors.Crimson);

        switch (quality)
        {
            case "Excellent":
                _sqiDot.Fill = green;
                break;
            case "Barely acceptable":
            case "Barely acceptable/Acceptable":
                _sqiDot.Fill = yellow;
                break;
            default:
                _sqiDot.Fill = red;
                break;
        }

        _sqiLabel.Text = $"Quality: {quality} ({primaryLead})";
        _sqiDetails.Text = $"sSQI (skew): {ssqi:F2} | kSQI (kurt): {ksqi:F2} | pSQI (flat): {psqi * 100:F1}%";
    }
}
