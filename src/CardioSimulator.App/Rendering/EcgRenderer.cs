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
    private const float CalAreaWidth = 48f;
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

    private static void DrawTrace(
        CanvasDrawingSession ds,
        IReadOnlyList<float> values,
        float xLeft,
        float baselineY,
        float stepX,
        float stepY)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(xLeft, baselineY - values[0] * stepY);
        for (var i = 1; i < values.Count; i++)
        {
            pb.AddLine(xLeft + i * stepX, baselineY - values[i] * stepY);
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geometry, EcgColors.Trace, TraceStroke, RoundStroke);
    }
}
