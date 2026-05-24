using System.Numerics;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// Win2D port of the Compose rendering pipeline (ekgGrid + ChartCanvas +
/// CalibrationPulse + Lead/LeadsGrid layout). Draws a full multi-lead monitor
/// in a single drawing-session pass.
/// </summary>
public static class EcgRenderer
{
    public const float CalAreaWidth = 48f;
    private const float SmallStroke = 0.5f;
    private const float LargeStroke = 1.5f;
    private const float TraceStroke = 1.5f;
    private const float CalStroke = 1.5f;

    /// <summary>
    /// px-per-mm anchor — faithful transcription of Android's
    /// <c>density * (160/25.4) * displayScale</c> (see docs/ecg-rendering-pipeline.md §4).
    /// Win2D rasterizes these DIP coordinates to physical pixels by the monitor scale,
    /// so the per-density factor is implicit. The 160 baseline (not WinUI's 96) keeps the
    /// dp-based constants (48 cal area, 1.5/0.5 strokes, 4 wing, 8 offset, 16 label) in the
    /// same proportion to the grid as Android.
    /// </summary>
    public static float PxPerMm(float displayScale) => (160f / 25.4f) * displayScale;

    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    public static void Render(
        CanvasDrawingSession ds,
        float width,
        float height,
        IReadOnlyDictionary<Lead, Points> waveforms,
        MonitorModeModel mode,
        float elapsedSeconds = 0f)
    {
        var scale = new PixelScale(PxPerMm(mode.DisplayScale), mode.Speed, 1f, mode.Calibration);
        var palette = EcgColors.Palette(mode.GridScheme);

        DrawGrid(ds, width, height, scale, palette);

        var count = mode.Count;
        if (count <= 0) return;

        var maxColumns = mode.SeriesScheme switch
        {
            SeriesScheme.OneColumn => 1,
            SeriesScheme.TwoColumn => 2,
            SeriesScheme.Grid => 4,
            _ => 1,
        };
        var rows = (int)Math.Ceiling(count / (float)maxColumns);
        if (rows <= 0) return;
        var columns = (int)Math.Ceiling(count / (float)rows);

        var cellW = width / columns;
        var cellH = height / rows;

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman", // serif analog of Compose FontFamily.Serif
            FontSize = 16f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        var leadOrder = Leads.All;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var itemIndex = col * rows + row; // column-major, matches LeadsGrid
                if (itemIndex >= count || itemIndex >= leadOrder.Count) continue;
                var lead = leadOrder[itemIndex];

                var cellX = col * cellW;
                var cellY = row * cellH;
                var baselineY = cellY + cellH / 2f;
                var traceLeft = cellX + CalAreaWidth;

                DrawCalibrationPulse(ds, cellX, baselineY, scale);
                ds.DrawText(lead.ToString(),
                    new Rect(cellX + 4, baselineY + 16, CalAreaWidth, 20), EcgColors.Label, textFormat);

                if (waveforms.TryGetValue(lead, out var points) && points.Values.Count >= 2)
                {
                    var traceWidth = (float)Math.Max(0, cellW - CalAreaWidth);
                    var clip = new Rect(traceLeft, cellY, traceWidth, cellH);
                    using (ds.CreateLayer(1f, clip))
                    {
                        DrawTrace(ds, points.Values, traceLeft, traceWidth, baselineY,
                            scale.PxPerSample, scale.PxPerAdcCount, scale.PxPerSec,
                            mode.IsRunning, elapsedSeconds);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draws a single editable lead: grid + calibration pulse + label + a static trace plus
    /// blue drag-handle dots over each (subsampled) sample. Port of the Android
    /// <c>EditableLead</c> (ChartCanvas + SampleHandleOverlay).
    /// </summary>
    public static void RenderEditableLead(
        CanvasDrawingSession ds,
        float width,
        float height,
        LeadStream stream,
        int baseline,
        MonitorModeModel mode)
    {
        var scale = new PixelScale(PxPerMm(mode.DisplayScale), mode.Speed, 1f, mode.Calibration);
        var palette = EcgColors.Palette(mode.GridScheme);
        DrawGrid(ds, width, height, scale, palette);

        var baselineY = height / 2f;
        var traceLeft = CalAreaWidth;

        DrawCalibrationPulse(ds, 0f, baselineY, scale);
        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = 16f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };
        ds.DrawText(stream.Lead.ToString(),
            new Rect(4, baselineY + 16, CalAreaWidth, 20), EcgColors.Label, textFormat);

        var samples = stream.Samples;
        if (samples.Length < 2) return;

        var stepX = scale.PxPerSample;
        var stepY = scale.PxPerAdcCount;
        var clip = new Rect(traceLeft, 0, Math.Max(0, width - traceLeft), height);
        using (ds.CreateLayer(1f, clip))
        {
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(traceLeft, baselineY - (samples[0] - baseline) * stepY);
            for (var i = 1; i < samples.Length; i++)
            {
                pb.AddLine(traceLeft + i * stepX, baselineY - (samples[i] - baseline) * stepY);
            }
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geometry, EcgColors.Trace, TraceStroke, RoundStroke);

            // Drag handles (subsampled so they don't clutter when stepX is small).
            const float minSpacing = 8f;
            const float radius = 3f;
            var stride = stepX < minSpacing ? Math.Max(1, (int)Math.Ceiling(minSpacing / stepX)) : 1;
            var handleColor = new Color { A = 128, R = 0, G = 0, B = 255 };
            for (var i = 0; i < samples.Length; i += stride)
            {
                var x = traceLeft + i * stepX;
                var y = baselineY - (samples[i] - baseline) * stepY;
                ds.FillCircle(x, y, radius, handleColor);
            }
        }
    }

    private static void DrawGrid(
        CanvasDrawingSession ds, float width, float height, PixelScale scale, GridPalette palette)
    {
        ds.Clear(palette.Background);

        var small = scale.SmallGridStepPx;
        var large = scale.LargeGridStepPx;
        if (small <= 0) return;

        for (var x = 0f; x <= width; x += small)
            ds.DrawLine(x, 0, x, height, palette.SmallLine, SmallStroke);
        for (var y = 0f; y <= height; y += small)
            ds.DrawLine(0, y, width, y, palette.SmallLine, SmallStroke);
        for (var x = 0f; x <= width; x += large)
            ds.DrawLine(x, 0, x, height, palette.LargeLine, LargeStroke);
        for (var y = 0f; y <= height; y += large)
            ds.DrawLine(0, y, width, y, palette.LargeLine, LargeStroke);
    }

    private static void DrawCalibrationPulse(
        CanvasDrawingSession ds, float cellX, float baselineY, PixelScale scale)
    {
        var pulseHeight = 1f * scale.PxPerMv;
        var pulseWidth = 0.2f * scale.PxPerSec;
        var startX = cellX + 8f;
        const float wing = 4f;

        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(startX, baselineY);
        pb.AddLine(startX + wing, baselineY);
        pb.AddLine(startX + wing, baselineY - pulseHeight);
        pb.AddLine(startX + wing + pulseWidth, baselineY - pulseHeight);
        pb.AddLine(startX + wing + pulseWidth, baselineY);
        pb.AddLine(startX + wing + pulseWidth + wing, baselineY);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geometry, EcgColors.Trace, CalStroke);
    }

    /// <summary>
    /// Draws the lead trace tiled across the trace area and scrolling left at paper speed
    /// when running — faithful to the Android <c>PreviewPane</c> (one loop period =
    /// max(1s of paper, the data width), so sub-second rhythms repeat with a gap).
    /// </summary>
    private static void DrawTrace(
        CanvasDrawingSession ds,
        IReadOnlyList<float> values,
        float xLeft,
        float traceWidth,
        float baselineY,
        float stepX,
        float stepY,
        float pxPerSec,
        bool isRunning,
        float elapsedSeconds)
    {
        // Build the waveform once (x relative to 0, y baked to the absolute baseline).
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(0f, baselineY - values[0] * stepY);
        for (var i = 1; i < values.Count; i++)
        {
            pb.AddLine(i * stepX, baselineY - values[i] * stepY);
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(pb);

        var dataWidth = values.Count * stepX;
        var periodPx = Math.Max(pxPerSec, dataWidth);
        if (periodPx <= 0) return;

        var xOffset = isRunning ? -(float)(elapsedSeconds * pxPerSec % periodPx) : 0f;
        var iterations = (int)(traceWidth / periodPx) + 2;

        var original = ds.Transform;
        for (var i = 0; i <= iterations; i++)
        {
            ds.Transform = Matrix3x2.CreateTranslation(xLeft + xOffset + i * periodPx, 0f);
            ds.DrawGeometry(geometry, EcgColors.Trace, TraceStroke, RoundStroke);
        }
        ds.Transform = original;
    }
}
