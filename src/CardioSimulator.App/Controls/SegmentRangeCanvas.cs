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
public enum SegmentTool { Range, VerticalLine, HorizontalLine, Label, Point, Delete }

/// <summary>
/// Interactive picker for an ECG segment: shows a lead's full waveform on a grid with a draggable
/// <b>selection band</b> (defining the start/duration window) and lets the author drop guide lines, text
/// labels, and points onto it (<see cref="TipOverlay"/>s in ECG data space — sample index + baseline-relative
/// ADC, matching the lecture renderer). Raises <see cref="RangeChanged"/> / <see cref="TipsChanged"/>.
/// Draws off-screen via <see cref="CanvasImageSource"/> into an <see cref="Image"/> so it renders reliably
/// even inside a <c>ContentDialog</c> popup (a plain Win2D <c>CanvasControl</c> does not).
/// </summary>
public sealed class SegmentRangeCanvas : UserControl
{
    private const float CanvasW = 620f, CanvasH = 190f, Pad = 10f;

    private readonly Image _image = new() { Width = CanvasW, Height = CanvasH };
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private CanvasImageSource? _surface;

    private IReadOnlyList<float> _values = Array.Empty<float>();
    private float _maxAbs = 1f;
    private readonly List<TipOverlay> _tips = new();

    private int _start;
    private int _window = 1;

    private enum Drag { None, MoveBand, ResizeStart, ResizeEnd }
    private Drag _drag = Drag.None;
    private double _dragGrabSample;

    private const string Pink = "#FFF5F5", PinkLine = "#F3B9B9", Trace = "#111111";
    private static readonly Color BandFill = Color.FromArgb(0x33, 0x46, 0x82, 0xB4);
    private static readonly Color BandEdge = Color.FromArgb(0xCC, 0x2c, 0x5f, 0x9a);
    private static readonly Color TipColor = Color.FromArgb(0xFF, 0x15, 0x65, 0xC0);

    public SegmentTool Tool { get; set; } = SegmentTool.Range;
    public string LabelText { get; set; } = string.Empty;
    public float SampleRateHz { get; private set; } = 500f;

    public int StartSample => _start;
    public int WindowSamples => _window;
    public double StartSec => _start / SampleRateHz;
    public double DurationSec => _window / SampleRateHz;
    public IReadOnlyList<TipOverlay> Tips => _tips.ToList();

    public event Action? RangeChanged;
    public event Action? TipsChanged;

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
        Loaded += (_, _) => { _surface = CreateSurface(); Redraw(); };
    }

    /// <summary>Loads a waveform and the initial window/tips (samples clamped to the data).</summary>
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
        Redraw();
    }

    public void ClearTips()
    {
        if (_tips.Count == 0) return;
        _tips.Clear();
        TipsChanged?.Invoke();
        Redraw();
    }

    // ── coordinate mapping (canvas ↔ data space) ────────────────────────────────

    private const float PlotW = CanvasW - 2 * Pad;
    private const float BaselineY = CanvasH / 2f;
    private float YScale => Math.Max(1f, CanvasH / 2f - Pad) / _maxAbs;
    private int LastSample => Math.Max(1, _values.Count - 1);

    private float SampleToX(double s) => Pad + (float)(s / LastSample) * PlotW;
    private double XToSample(double x) => Math.Clamp((x - Pad) / PlotW * LastSample, 0, LastSample);
    private float AdcToY(double a) => BaselineY - (float)a * YScale;
    private double YToAdc(double y) => (BaselineY - y) / YScale;

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

        // Light grid.
        var grid = ColorFromHex(PinkLine);
        for (var gx = Pad; gx <= CanvasW - Pad; gx += 30f) ds.DrawLine(gx, Pad, gx, CanvasH - Pad, grid, 0.5f);
        for (var gy = Pad; gy <= CanvasH - Pad; gy += 30f) ds.DrawLine(Pad, gy, CanvasW - Pad, gy, grid, 0.5f);
        ds.DrawLine(Pad, BaselineY, CanvasW - Pad, BaselineY, grid, 1f);

        // Selection band + edges.
        var x0 = SampleToX(_start);
        var x1 = SampleToX(_start + _window);
        ds.FillRectangle(x0, Pad, Math.Max(1f, x1 - x0), CanvasH - 2 * Pad, BandFill);
        ds.DrawLine(x0, Pad, x0, CanvasH - Pad, BandEdge, 2f);
        ds.DrawLine(x1, Pad, x1, CanvasH - Pad, BandEdge, 2f);

        // Waveform (decimated).
        var step = Math.Max(1, (int)(_values.Count / (PlotW * 2)));
        using (var pb = new CanvasPathBuilder(ds))
        {
            pb.BeginFigure(SampleToX(0), AdcToY(_values[0]));
            for (var i = step; i < _values.Count; i += step) pb.AddLine(SampleToX(i), AdcToY(_values[i]));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, ColorFromHex(Trace), 1.2f);
        }

        // Tips.
        using var font = new CanvasTextFormat { FontFamily = "Times New Roman", FontSize = 13, WordWrapping = CanvasWordWrapping.NoWrap };
        foreach (var tip in _tips)
        {
            switch (tip.Kind)
            {
                case TipOverlayKind.VerticalLines:
                    foreach (var p in tip.Points) ds.DrawLine(SampleToX(p.Sample), Pad, SampleToX(p.Sample), CanvasH - Pad, TipColor, 1.4f);
                    break;
                case TipOverlayKind.HorizontalLines:
                    foreach (var p in tip.Points) ds.DrawLine(Pad, AdcToY(p.Adc), CanvasW - Pad, AdcToY(p.Adc), TipColor, 1.4f);
                    break;
                case TipOverlayKind.Label when tip.Points.Count >= 1:
                    ds.DrawText(tip.Text ?? "…", SampleToX(tip.Points[0].Sample), AdcToY(tip.Points[0].Adc) - 8, TipColor, font);
                    break;
                case TipOverlayKind.Points:
                    foreach (var p in tip.Points) ds.FillCircle(SampleToX(p.Sample), AdcToY(p.Adc), 4f, TipColor);
                    break;
            }
        }
    }

    // ── interaction ─────────────────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_values.Count == 0) return;
        var pos = e.GetCurrentPoint(_root).Position;
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
                AddTip(new TipOverlay(TipOverlayKind.VerticalLines, new[] { new TipPoint(s, 0) }));
                break;
            case SegmentTool.HorizontalLine:
                AddTip(new TipOverlay(TipOverlayKind.HorizontalLines, new[] { new TipPoint(s, (float)adc) }));
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
        _drag = Drag.None;
        _root.ReleasePointerCapture(e.Pointer);
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
