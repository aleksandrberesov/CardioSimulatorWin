using System.Diagnostics;
using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// A multi-lead ECG monitor backed by a Win2D <see cref="CanvasControl"/>. A 30fps timer
/// invalidates the surface while running so the trace scrolls; static frames redraw on
/// data/mode change. (A plain <see cref="CanvasControl"/> — not CanvasAnimatedControl — is
/// used deliberately so the window stays capturable via PrintWindow for verification.)
/// </summary>
public sealed class EcgMonitorControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IReadOnlyDictionary<Lead, Points> _waveforms = new Dictionary<Lead, Points>();
    private MonitorModeModel _mode = new();
    private IReadOnlyList<SignificantPoint> _significantPoints = Array.Empty<SignificantPoint>();
    private IReadOnlyDictionary<int, Points> _comparisonWaveforms = new Dictionary<int, Points>();
    private IReadOnlyDictionary<int, string> _comparisonLabels = new Dictionary<int, string>();

    public EcgMonitorControl()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => { if (_mode.IsRunning) _canvas.Invalidate(); };
        _timer.Start();

        Unloaded += (_, _) => { _timer.Stop(); _canvas.RemoveFromVisualTree(); };
    }

    public IReadOnlyDictionary<Lead, Points> Waveforms
    {
        get => _waveforms;
        set { _waveforms = value; _canvas.Invalidate(); }
    }

    public MonitorModeModel Mode
    {
        get => _mode;
        set { _mode = value; _canvas.Invalidate(); }
    }

    public IReadOnlyList<SignificantPoint> SignificantPoints
    {
        get => _significantPoints;
        set { _significantPoints = value; _canvas.Invalidate(); }
    }

    /// <summary>Comparison waveforms, keyed by pane index (compare mode).</summary>
    public IReadOnlyDictionary<int, Points> ComparisonWaveforms
    {
        get => _comparisonWaveforms;
        set { _comparisonWaveforms = value; _canvas.Invalidate(); }
    }

    /// <summary>Display labels (pathology name) per pane index, for compare-mode cell titles.</summary>
    public IReadOnlyDictionary<int, string> ComparisonLabels
    {
        get => _comparisonLabels;
        set { _comparisonLabels = value; _canvas.Invalidate(); }
    }

    /// <summary>Maps a point (in this control's coordinates) to a pane index, or -1 if none.</summary>
    public int PaneIndexAt(double x, double y) =>
        EcgRenderer.PaneIndexAt((float)_canvas.Size.Width, (float)_canvas.Size.Height, _mode, x, y);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EcgRenderer.Render(
            args.DrawingSession,
            (float)sender.Size.Width,
            (float)sender.Size.Height,
            _waveforms,
            _mode,
            (float)_clock.Elapsed.TotalSeconds,
            _significantPoints,
            _comparisonWaveforms,
            _comparisonLabels);
    }
}
