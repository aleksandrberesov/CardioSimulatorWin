using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>The active pointer tool in <see cref="SegmentRangeCanvas"/>.</summary>
public enum SegmentTool { Range, VerticalLine, HorizontalLine, Label, Point, Delete, Pan, Crop }

/// <summary>
/// Interactive picker for an ECG segment: shows a lead's full waveform on a grid with a draggable
/// <b>selection band</b> (defining the start/duration window) and lets the author drop guide lines, text
/// labels, and points onto it (<see cref="TipOverlay"/>s in ECG data space — sample index + baseline-relative
/// ADC, matching the lecture renderer). Raises <see cref="RangeChanged"/> / <see cref="TipsChanged"/>.
/// <para>
/// A <b>view transform</b> (independent X/Y zoom, pan, and a crop-to-rectangle gesture) magnifies the strip
/// for precise placement. It is purely a visual aid: it never changes the emitted segment (which stays the
/// start/duration window), so the <see cref="ZoomX"/>/<see cref="ZoomY"/>/pan state is not persisted.
/// </para>
/// Draws off-screen via <see cref="CanvasImageSource"/> into an <see cref="Image"/> so it renders reliably
/// even inside a <c>ContentDialog</c> popup (a plain Win2D <c>CanvasControl</c> does not).
/// </summary>
public sealed class SegmentRangeCanvas : UserControl
{
    private const float CanvasW = 620f, CanvasH = 190f, Pad = 10f;
    private const double MaxZoom = 12.0;

    private readonly Image _image = new() { Width = CanvasW, Height = CanvasH };
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private CanvasImageSource? _surface;

    private IReadOnlyList<float> _values = Array.Empty<float>();
    private float _maxAbs = 1f;
    private readonly List<TipOverlay> _tips = new();

    private int _start;
    private int _window = 1;

    // View transform: screen = base * scale + translate (independent X/Y zoom + pan). Identity = fit.
    private double _scaleX = 1, _scaleY = 1, _tx, _ty;

    private enum Drag { None, MoveBand, ResizeStart, ResizeEnd, PanView, CropRect, DrawLine }
    private Drag _drag = Drag.None;
    private double _dragGrabSample;
    private double _panGrabX, _panGrabY, _panStartTx, _panStartTy;
    private Point _cropStart, _cropEnd;
    private SegmentTool _lineTool;          // which line kind is being dragged
    private TipPoint _lineStart, _lineEnd;  // in-progress point-to-point line (data space)

    private const string Pink = "#FFF5F5", PinkLine = "#F3B9B9", Trace = "#111111";
    private static readonly Color BandFill = Color.FromArgb(0x33, 0x46, 0x82, 0xB4);
    private static readonly Color BandEdge = Color.FromArgb(0xCC, 0x2c, 0x5f, 0x9a);
    private static readonly Color TipColor = Color.FromArgb(0xFF, 0x15, 0x65, 0xC0);
    private static readonly Color CropFill = Color.FromArgb(0x22, 0x2c, 0x5f, 0x9a);
    private static readonly CanvasStrokeStyle CropStroke = new() { DashStyle = CanvasDashStyle.Dash };

    public SegmentTool Tool { get; set; } = SegmentTool.Range;
    public string LabelText { get; set; } = string.Empty;
    public float SampleRateHz { get; private set; } = 500f;

    public int StartSample => _start;
    public int WindowSamples => _window;
    public double StartSec => _start / SampleRateHz;
    public double DurationSec => _window / SampleRateHz;
    public IReadOnlyList<TipOverlay> Tips => _tips.ToList();

    /// <summary>Current independent zoom factors (1 = fit the whole strip). View-only — the emitted
    /// segment is unaffected.</summary>
    public double ZoomX => _scaleX;
    public double ZoomY => _scaleY;

    public event Action? RangeChanged;
    public event Action? TipsChanged;
    /// <summary>Raised when the zoom/pan view changes (slider, crop, or reset) so a host can sync
    /// its zoom controls to <see cref="ZoomX"/>/<see cref="ZoomY"/>.</summary>
    public event Action? ViewChanged;

    public SegmentRangeCanvas()
    {
        Width = CanvasW;
        Height = CanvasH;
        _root.Children.Add(_image);
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        Content = _root;
        // Recreate the surface once we're in the tree so it picks up the real display DPI (sharp on hi-DPI).
        Loaded += (_, _) =>
        {
            _surface = CreateSurface();
            Theming.AppTheme.Changed += OnThemeChanged;
            Redraw();
        };
        Unloaded += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => Redraw();

    /// <summary>Loads a waveform and the initial window/tips (samples clamped to the data). Resets the
    /// zoom/pan view so the freshly loaded strip is shown in full.</summary>
    public void Load(IReadOnlyList<float> values, float sampleRateHz, int startSample, int windowSamples, IEnumerable<TipOverlay> tips)
    {
        _values = values ?? Array.Empty<float>();
        SampleRateHz = sampleRateHz <= 0 ? 500f : sampleRateHz;
        _maxAbs = _values.Count == 0 ? 1f : Math.Max(1f, _values.Max(Math.Abs));
        _tips.Clear();
        _tips.AddRange(tips);
        var n = Math.Max(1, _values.Count);
        _start = Math.Clamp(startSample, 0, Math.Max(0, n - 1));
        _window = Math.Clamp(windowSamples, 1, n - _start);
        _scaleX = _scaleY = 1; _tx = _ty = 0;
        ViewChanged?.Invoke();
        Redraw();
    }

    public void ClearTips()
    {
        if (_tips.Count == 0) return;
        _tips.Clear();
        TipsChanged?.Invoke();
        Redraw();
    }

    // ── zoom / pan / crop (view only — never touches the segment data) ────────────

    /// <summary>Sets the horizontal (time) zoom, anchored on the current view centre.</summary>
    public void SetZoomX(double zoom) => ApplyZoom(Math.Clamp(zoom, 1, MaxZoom), _scaleY);

    /// <summary>Sets the vertical (amplitude) zoom, anchored on the current view centre.</summary>
    public void SetZoomY(double zoom) => ApplyZoom(_scaleX, Math.Clamp(zoom, 1, MaxZoom));

    /// <summary>Restores the 1:1 fit view (no zoom, no pan).</summary>
    public void ResetView()
    {
        _scaleX = _scaleY = 1; _tx = _ty = 0;
        ViewChanged?.Invoke();
        Redraw();
    }

    private void ApplyZoom(double newX, double newY)
    {
        // Keep the point currently under the view centre fixed, so zooming feels centred.
        const double cx = CanvasW / 2.0, cy = CanvasH / 2.0;
        var baseCx = (cx - _tx) / _scaleX;
        var baseCy = (cy - _ty) / _scaleY;
        _scaleX = newX; _scaleY = newY;
        _tx = cx - baseCx * _scaleX;
        _ty = cy - baseCy * _scaleY;
        ClampPan();
        ViewChanged?.Invoke();
        Redraw();
    }

    // Keep the base content [0..CanvasW]×[0..CanvasH] covering the canvas — no scrolling into empty space.
    private void ClampPan()
    {
        _tx = Math.Clamp(_tx, CanvasW * (1 - _scaleX), 0);
        _ty = Math.Clamp(_ty, CanvasH * (1 - _scaleY), 0);
    }

    // Zooms the view so the just-dragged crop rectangle fills the canvas (clamped to MaxZoom).
    private void ApplyCrop()
    {
        var r = CropRect();
        if (r.Width < 8 || r.Height < 8) { Redraw(); return; } // ignore an accidental click / tiny box
        var bx0 = (r.Left - _tx) / _scaleX; var bx1 = (r.Right - _tx) / _scaleX;
        var by0 = (r.Top - _ty) / _scaleY; var by1 = (r.Bottom - _ty) / _scaleY;
        _scaleX = Math.Clamp(CanvasW / Math.Max(1e-3, bx1 - bx0), 1, MaxZoom);
        _scaleY = Math.Clamp(CanvasH / Math.Max(1e-3, by1 - by0), 1, MaxZoom);
        var bcx = (bx0 + bx1) / 2.0; var bcy = (by0 + by1) / 2.0;
        _tx = CanvasW / 2.0 - bcx * _scaleX;
        _ty = CanvasH / 2.0 - bcy * _scaleY;
        ClampPan();
        ViewChanged?.Invoke();
        Redraw();
    }

    private Rect CropRect()
    {
        var x = Math.Min(_cropStart.X, _cropEnd.X);
        var y = Math.Min(_cropStart.Y, _cropEnd.Y);
        return new Rect(x, y, Math.Abs(_cropEnd.X - _cropStart.X), Math.Abs(_cropEnd.Y - _cropStart.Y));
    }

    // ── coordinate mapping (base canvas space ↔ data space, then the view transform) ─

    private const float PlotW = CanvasW - 2 * Pad;
    private const float BaselineY = CanvasH / 2f;
    private float YScale => Math.Max(1f, CanvasH / 2f - Pad) / _maxAbs;
    private int LastSample => Math.Max(1, _values.Count - 1);

    // base → screen (apply zoom/pan); screen → base is the inverse used by the hit-testing helpers.
    private float TX(double baseX) => (float)(baseX * _scaleX + _tx);
    private float TY(double baseY) => (float)(baseY * _scaleY + _ty);
    private float SampleToX(double s) => TX(Pad + s / LastSample * PlotW);
    private float AdcToY(double a) => TY(BaselineY - a * YScale);
    private double XToSample(double x) => Math.Clamp(((x - _tx) / _scaleX - Pad) / PlotW * LastSample, 0, LastSample);
    private double YToAdc(double y) => (BaselineY - (y - _ty) / _scaleY) / YScale;

    // ── drawing (off-screen) ────────────────────────────────────────────────────

    private void Redraw()
    {
        try
        {
            _surface ??= CreateSurface();
            using var ds = _surface.CreateDrawingSession(ColorFromHex(Pink));
            Draw(ds);
        }
        catch (Exception) // device lost — recreate and retry once
        {
            try
            {
                _surface = CreateSurface();
                using var ds = _surface.CreateDrawingSession(ColorFromHex(Pink));
                Draw(ds);
            }
            catch { /* give up this frame */ }
        }
    }

    private CanvasImageSource CreateSurface()
    {
        var dpi = XamlRoot is { } root ? (float)(root.RasterizationScale * 96.0) : 96f;
        var src = new CanvasImageSource(CanvasDevice.GetSharedDevice(), CanvasW, CanvasH, dpi);
        _image.Source = src;
        return src;
    }

    private void Draw(CanvasDrawingSession ds)
    {
        if (_values.Count == 0)
        {
            using var f0 = new CanvasTextFormat { FontSize = 13 };
            ds.DrawText("Choose a rhythm to see its waveform.", Pad, CanvasH / 2 - 8, Colors.Gray, f0);
            return;
        }

        // Light grid (drawn in content space, so it scales/pans with the trace like zoomed ECG paper).
        var grid = ColorFromHex(PinkLine);
        for (var gx = Pad; gx <= CanvasW - Pad; gx += 30f) ds.DrawLine(TX(gx), TY(Pad), TX(gx), TY(CanvasH - Pad), grid, 0.5f);
        for (var gy = Pad; gy <= CanvasH - Pad; gy += 30f) ds.DrawLine(TX(Pad), TY(gy), TX(CanvasW - Pad), TY(gy), grid, 0.5f);
        ds.DrawLine(TX(Pad), TY(BaselineY), TX(CanvasW - Pad), TY(BaselineY), grid, 1f);

        // Selection band + edges.
        var x0 = SampleToX(_start);
        var x1 = SampleToX(_start + _window);
        var top = TY(Pad);
        var bottom = TY(CanvasH - Pad);
        ds.FillRectangle(x0, top, Math.Max(1f, x1 - x0), bottom - top, BandFill);
        ds.DrawLine(x0, top, x0, bottom, BandEdge, 2f);
        ds.DrawLine(x1, top, x1, bottom, BandEdge, 2f);

        // Waveform (decimated; finer when zoomed in on the time axis).
        var step = Math.Max(1, (int)(_values.Count / (PlotW * 2 * _scaleX)));
        using (var pb = new CanvasPathBuilder(ds))
        {
            pb.BeginFigure(SampleToX(0), AdcToY(_values[0]));
            for (var i = step; i < _values.Count; i += step) pb.AddLine(SampleToX(i), AdcToY(_values[i]));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            var traceColor = ColorFromHex(Theming.AppTheme.IsDark ? "#FFFFFF" : Trace);
            ds.DrawGeometry(geo, traceColor, 1.2f);
        }

        // Tips.
        using var font = new CanvasTextFormat { FontFamily = "Times New Roman", FontSize = 13, WordWrapping = CanvasWordWrapping.NoWrap };
        foreach (var tip in _tips)
        {
            switch (tip.Kind)
            {
                case TipOverlayKind.VerticalLines:
                    // Point-to-point: vertical segment at pts[0]'s x, bounded by pts[0]..pts[1] amplitude.
                    // Legacy single-point tips still span the whole cell height.
                    if (tip.Points.Count >= 2)
                    {
                        var x = SampleToX(tip.Points[0].Sample);
                        ds.DrawLine(x, AdcToY(tip.Points[0].Adc), x, AdcToY(tip.Points[1].Adc), TipColor, 1.4f);
                    }
                    else foreach (var p in tip.Points) ds.DrawLine(SampleToX(p.Sample), TY(Pad), SampleToX(p.Sample), TY(CanvasH - Pad), TipColor, 1.4f);
                    break;
                case TipOverlayKind.HorizontalLines:
                    // Point-to-point: horizontal segment at pts[0]'s amplitude, bounded by pts[0]..pts[1] x.
                    // Legacy single-point tips still span the whole cell width.
                    if (tip.Points.Count >= 2)
                    {
                        var y = AdcToY(tip.Points[0].Adc);
                        ds.DrawLine(SampleToX(tip.Points[0].Sample), y, SampleToX(tip.Points[1].Sample), y, TipColor, 1.4f);
                    }
                    else foreach (var p in tip.Points) ds.DrawLine(TX(Pad), AdcToY(p.Adc), TX(CanvasW - Pad), AdcToY(p.Adc), TipColor, 1.4f);
                    break;
                case TipOverlayKind.Label when tip.Points.Count >= 1:
                    ds.DrawText(tip.Text ?? "…", SampleToX(tip.Points[0].Sample), AdcToY(tip.Points[0].Adc) - 8, TipColor, font);
                    break;
                case TipOverlayKind.Points:
                    foreach (var p in tip.Points) ds.FillCircle(SampleToX(p.Sample), AdcToY(p.Adc), 4f, TipColor);
                    break;
            }
        }

        // In-progress line being dragged (point-to-point, snapped to its axis).
        if (_drag == Drag.DrawLine)
        {
            if (_lineTool == SegmentTool.VerticalLine)
            {
                var x = SampleToX(_lineStart.Sample);
                ds.DrawLine(x, AdcToY(_lineStart.Adc), x, AdcToY(_lineEnd.Adc), TipColor, 1.4f);
            }
            else
            {
                var y = AdcToY(_lineStart.Adc);
                ds.DrawLine(SampleToX(_lineStart.Sample), y, SampleToX(_lineEnd.Sample), y, TipColor, 1.4f);
            }
        }

        // In-progress crop marquee (screen space — it selects a view region, not data).
        if (_drag == Drag.CropRect)
        {
            var r = CropRect();
            ds.FillRectangle(r, CropFill);
            ds.DrawRectangle(r, BandEdge, 1.4f, CropStroke);
        }
    }

    // ── interaction ─────────────────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_values.Count == 0) return;
        var pos = e.GetCurrentPoint(_root).Position;

        // View tools grab the pointer and never touch the segment data.
        switch (Tool)
        {
            case SegmentTool.Pan:
                _drag = Drag.PanView;
                _panGrabX = pos.X; _panGrabY = pos.Y; _panStartTx = _tx; _panStartTy = _ty;
                _root.CapturePointer(e.Pointer);
                return;
            case SegmentTool.Crop:
                _drag = Drag.CropRect;
                _cropStart = pos; _cropEnd = pos;
                _root.CapturePointer(e.Pointer);
                return;
        }

        var sample = XToSample(pos.X);
        var adc = YToAdc(pos.Y);

        if (Tool == SegmentTool.Range)
        {
            var x0 = SampleToX(_start);
            var x1 = SampleToX(_start + _window);
            if (Math.Abs(pos.X - x0) <= 8) _drag = Drag.ResizeStart;
            else if (Math.Abs(pos.X - x1) <= 8) _drag = Drag.ResizeEnd;
            else if (pos.X > x0 && pos.X < x1) { _drag = Drag.MoveBand; _dragGrabSample = sample - _start; }
            else _drag = Drag.None;
            if (_drag != Drag.None) _root.CapturePointer(e.Pointer);
            return;
        }

        var s = (float)sample;
        switch (Tool)
        {
            case SegmentTool.VerticalLine:
            case SegmentTool.HorizontalLine:
                // Drag to draw the line point-to-point (bounded), instead of dropping a full-cell guide.
                _drag = Drag.DrawLine;
                _lineTool = Tool;
                _lineStart = new TipPoint(s, (float)adc);
                _lineEnd = _lineStart;
                _root.CapturePointer(e.Pointer);
                break;
            case SegmentTool.Point:
                AddTip(new TipOverlay(TipOverlayKind.Points, new[] { new TipPoint(s, (float)adc) }));
                break;
            case SegmentTool.Label:
                AddTip(new TipOverlay(TipOverlayKind.Label, new[] { new TipPoint(s, (float)adc) },
                    Text: string.IsNullOrWhiteSpace(LabelText) ? "label" : LabelText.Trim()));
                break;
            case SegmentTool.Delete:
                DeleteNearest(pos);
                break;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var pos = e.GetCurrentPoint(_root).Position;

        if (_drag == Drag.PanView)
        {
            _tx = _panStartTx + (pos.X - _panGrabX);
            _ty = _panStartTy + (pos.Y - _panGrabY);
            ClampPan();
            Redraw();
            return;
        }
        if (_drag == Drag.CropRect)
        {
            _cropEnd = pos;
            Redraw();
            return;
        }
        if (_drag == Drag.DrawLine)
        {
            _lineEnd = new TipPoint((float)XToSample(pos.X), (float)YToAdc(pos.Y));
            Redraw();
            return;
        }

        var sample = (int)Math.Round(XToSample(pos.X));
        var n = _values.Count;
        switch (_drag)
        {
            case Drag.MoveBand:
                _start = Math.Clamp((int)Math.Round(sample - _dragGrabSample), 0, Math.Max(0, n - _window));
                break;
            case Drag.ResizeStart:
                var end = _start + _window;
                _start = Math.Clamp(sample, 0, end - 1);
                _window = end - _start;
                break;
            case Drag.ResizeEnd:
                _window = Math.Clamp(sample - _start, 1, n - _start);
                break;
        }
        RangeChanged?.Invoke();
        Redraw();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        var finishing = _drag;
        _drag = Drag.None;
        _root.ReleasePointerCapture(e.Pointer);
        if (finishing == Drag.CropRect) { ApplyCrop(); return; }
        if (finishing == Drag.DrawLine)
        {
            // Snap the end so the segment stays axis-aligned: vertical keeps the start's sample (x) and spans
            // the dragged amplitude; horizontal keeps the start's amplitude (y) and spans the dragged x.
            var vertical = _lineTool == SegmentTool.VerticalLine;
            var end = vertical
                ? new TipPoint(_lineStart.Sample, _lineEnd.Adc)
                : new TipPoint(_lineEnd.Sample, _lineStart.Adc);
            if (!DegenerateLine(_lineStart, end))
                AddTip(new TipOverlay(vertical ? TipOverlayKind.VerticalLines : TipOverlayKind.HorizontalLines,
                    new[] { _lineStart, end }));
            else
                Redraw(); // too short — discard, and clear the in-progress preview
        }
    }

    /// <summary>A line drag shorter than a few pixels is treated as an accidental click and discarded.</summary>
    private bool DegenerateLine(TipPoint a, TipPoint b)
    {
        double dx = SampleToX(b.Sample) - SampleToX(a.Sample);
        double dy = AdcToY(b.Adc) - AdcToY(a.Adc);
        return Math.Sqrt(dx * dx + dy * dy) < 6.0;
    }

    private void AddTip(TipOverlay tip)
    {
        _tips.Add(tip);
        TipsChanged?.Invoke();
        Redraw();
    }

    private void DeleteNearest(Point pos)
    {
        var best = -1;
        var bestD = 14.0;
        for (var i = 0; i < _tips.Count; i++)
        {
            var t = _tips[i];
            foreach (var p in t.Points)
            {
                double d;
                if (t.Kind == TipOverlayKind.VerticalLines) d = Math.Abs(pos.X - SampleToX(p.Sample));
                else if (t.Kind == TipOverlayKind.HorizontalLines) d = Math.Abs(pos.Y - AdcToY(p.Adc));
                else { double dx = pos.X - SampleToX(p.Sample), dy = pos.Y - AdcToY(p.Adc); d = Math.Sqrt(dx * dx + dy * dy); }
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        if (best >= 0)
        {
            _tips.RemoveAt(best);
            TipsChanged?.Invoke();
            Redraw();
        }
    }

    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        var r = Convert.ToByte(hex.Substring(0, 2), 16);
        var g = Convert.ToByte(hex.Substring(2, 2), 16);
        var b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(0xFF, r, g, b);
    }
}
