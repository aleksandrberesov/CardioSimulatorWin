using System.ComponentModel;
using CardioSimulator.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
    }

    public EcgMonitorControl Monitor => _monitor;

    public void Bind(MonitorViewModel monitorVm, RhythmViewModel rhythmVm)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _monitor.Mode = monitorVm.MonitorMode;
        _monitor.Waveforms = rhythmVm.Waveforms;
        _monitor.SignificantPoints = rhythmVm.SignificantPoints;
        _scale = monitorVm.MonitorMode.Scale;
        _lastModeScale = _scale;
        ApplyTransform();
        monitorVm.PropertyChanged += OnMonitorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorViewModel.MonitorMode) || _monitorVm is null) return;
        var mode = _monitorVm.MonitorMode;
        _monitor.Mode = mode;

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
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Waveforms))
        {
            _monitor.Waveforms = _rhythmVm.Waveforms;
        }
        else if (e.PropertyName == nameof(RhythmViewModel.SignificantPoints))
        {
            _monitor.SignificantPoints = _rhythmVm.SignificantPoints;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
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
        _dragging = true;
        _lastPointer = e.GetCurrentPoint(this).Position;
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

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e);

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
}
