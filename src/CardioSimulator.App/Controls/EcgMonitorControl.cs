using System.Diagnostics;
using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

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

    // Zoom/pan are applied inside the Win2D draw (not via a XAML transform) so the trace stays
    // crisp and stroke widths stay visually constant at every zoom level.
    private float _viewZoom = 1f;
    private float _viewOffsetX;
    private float _viewOffsetY;

    // Ruler/caliper overlay: when active the user drags two points on the trace (in control DIPs)
    // and the gap is reported as a time interval (ms) + rate (bpm) and an amplitude (mV), derived
    // from the display PixelScale and the current zoom. Drawn on top of the rendered trace.
    private bool _rulerActive;
    private Point? _caliperA;
    private Point? _caliperB;
    private static readonly Color RulerColor = Color.FromArgb(255, 0x1E, 0x88, 0xE5);
    private static readonly Color RulerBand = Color.FromArgb(40, 0x1E, 0x88, 0xE5);
    private static readonly Color RulerBoxFill = Color.FromArgb(235, 255, 255, 255);
    private readonly CanvasTextFormat _rulerTextFormat = new() { FontSize = 13, WordWrapping = CanvasWordWrapping.NoWrap };

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

    /// <summary>Sets the zoom factor and pan offset (in DIPs) and redraws.</summary>
    public void SetView(float zoom, float offsetX, float offsetY)
    {
        _viewZoom = zoom;
        _viewOffsetX = offsetX;
        _viewOffsetY = offsetY;
        _canvas.Invalidate();
    }

    /// <summary>Enables/disables the ruler overlay. Turning it off clears any current measurement.</summary>
    public void SetRulerActive(bool active)
    {
        _rulerActive = active;
        if (!active) { _caliperA = null; _caliperB = null; }
        _canvas.Invalidate();
    }

    /// <summary>Sets the two caliper points (in control DIPs), or null to clear the measurement.</summary>
    public void SetCalipers(Point? a, Point? b)
    {
        _caliperA = a;
        _caliperB = b;
        _canvas.Invalidate();
    }

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
            _comparisonLabels,
            _viewZoom,
            _viewOffsetX,
            _viewOffsetY);

        if (_rulerActive)
            DrawRuler(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height);
    }

    /// <summary>
    /// Draws the caliper overlay: a translucent band + two vertical legs between the picked points,
    /// a connector, and a readout box. The time/voltage readout divides the on-screen gap by the
    /// zoomed pixel scale, so it reflects the real interval regardless of the current zoom level.
    /// </summary>
    private void DrawRuler(CanvasDrawingSession ds, float width, float height)
    {
        if (_caliperA is not { } a || _caliperB is not { } b) return;

        // EcgRenderer.Render leaves the zoom/pan transform applied; the calipers are captured in
        // screen DIPs, so reset to identity to draw them 1:1 over the rendered (already-zoomed) trace.
        ds.Transform = System.Numerics.Matrix3x2.Identity;

        var ax = (float)a.X; var ay = (float)a.Y;
        var bx = (float)b.X; var by = (float)b.Y;
        var x1 = Math.Min(ax, bx);
        var x2 = Math.Max(ax, bx);

        ds.FillRectangle(x1, 0, x2 - x1, height, RulerBand);
        ds.DrawLine(ax, 0, ax, height, RulerColor, 1.2f);
        ds.DrawLine(bx, 0, bx, height, RulerColor, 1.2f);
        ds.DrawLine(ax, ay, bx, by, RulerColor, 1.2f);
        ds.FillCircle(ax, ay, 4f, RulerColor);
        ds.FillCircle(bx, by, 4f, RulerColor);

        var scale = new PixelScale(EcgRenderer.EffectivePxPerMm(_mode), _mode.Speed, 1f, _mode.Calibration);
        var zoom = _viewZoom <= 0 ? 1f : _viewZoom;
        var dtSec = Math.Abs(bx - ax) / (scale.PxPerSec * zoom);
        var ms = dtSec * 1000.0;
        var mv = Math.Abs(by - ay) / (scale.PxPerMv * zoom);
        var rate = dtSec > 0 ? $"{60.0 / dtSec:0} bpm" : "— bpm";
        var text = $"Δt {ms:0} ms   {rate}\nΔ {mv:0.00} mV";

        const float boxW = 150f, boxH = 46f;
        const float boxGap = 8f;
        // Sit the readout just to the right of the blue band; if that would run off the
        // right edge, flip it to the left of the band. Clamp inside the control either way.
        var boxX = x2 + boxGap;
        if (boxX + boxW > width - 4f)
            boxX = x1 - boxGap - boxW;
        boxX = Math.Clamp(boxX, 4f, Math.Max(4f, width - boxW - 4f));
        const float boxY = 8f;
        ds.FillRoundedRectangle(boxX, boxY, boxW, boxH, 6f, 6f, RulerBoxFill);
        ds.DrawRoundedRectangle(boxX, boxY, boxW, boxH, 6f, 6f, RulerColor, 1f);
        ds.DrawText(text, new Rect(boxX + 8, boxY + 5, boxW - 12, boxH - 8), RulerColor, _rulerTextFormat);
    }
}
