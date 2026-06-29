using System.Numerics;
using CardioSimulator.App.Localization;
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
    /// <summary>Total left margin before the trace: a <see cref="LabelAreaWidth"/> strip holding
    /// the lead title, followed by the calibration pulse.</summary>
    public const float CalAreaWidth = 80f;
    /// <summary>Width of the lead-title strip at the very left of each cell (left of the pulse).
    /// Wide enough for 3-letter leads (aVR/aVL/aVF) at <see cref="LabelFontSize"/>.</summary>
    public const float LabelAreaWidth = 32f;
    private const float LabelFontSize = 14f;
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

    /// <summary>
    /// Builds the zoom/pan matrix applied to the drawing session: scale about the surface centre,
    /// then translate by the pan offset. Mirrors the inverse used by the controls' hit-testing.
    /// </summary>
    private static Matrix3x2 ViewTransform(float width, float height, float zoom, float offsetX, float offsetY) =>
        Matrix3x2.CreateScale(zoom, zoom, new Vector2(width / 2f, height / 2f))
        * Matrix3x2.CreateTranslation(offsetX, offsetY);

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
        float elapsedSeconds = 0f,
        IReadOnlyList<SignificantPoint>? significantPoints = null,
        IReadOnlyDictionary<int, Points>? comparisonWaveforms = null,
        IReadOnlyDictionary<int, string>? comparisonLabels = null,
        float viewZoom = 1f,
        float viewOffsetX = 0f,
        float viewOffsetY = 0f)
    {
        var scale = new PixelScale(PxPerMm(mode.DisplayScale), mode.Speed, 1f, mode.Calibration);
        var palette = EcgColors.Palette(mode.GridScheme, mode.BlankSheet);

        // Apply zoom/pan as a Win2D transform so the geometry stays crisp at any scale, then
        // counter-scale every stroke width by 1/zoom so line thickness looks the same at all zooms.
        var strokeScale = viewZoom > 0f ? 1f / viewZoom : 1f;
        ds.Transform = ViewTransform(width, height, viewZoom, viewOffsetX, viewOffsetY);

        // Blank sheet streams the trace left→right (+1); the gridded monitor scrolls
        // right→left (-1) as on a real scope.
        var streamSign = mode.BlankSheet ? 1f : -1f;

        // Grid scrolls with the trace when running (matches Android requirement).
        var gridOffset = mode.IsRunning ? streamSign * (float)(elapsedSeconds * scale.PxPerSec) : 0f;
        DrawGrid(ds, width, height, scale, palette, mode.BlankSheet, gridOffset, strokeScale);

        var count = mode.Count;
        if (count <= 0) return;

        var maxColumns = mode.SeriesScheme.MaxColumns();
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
        // Lead title sits in the left strip, vertically centered on the baseline (left of the pulse).
        using var labelFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = LabelFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // Explicit handpicked leads (e.g. from an <ecg> embed) take precedence over the default
        // first-N canonical order.
        var leadOrder = mode.LeadSelection is { Count: > 0 } ? mode.LeadSelection : Leads.All;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var itemIndex = col * rows + row; // column-major, matches LeadsGrid
                if (itemIndex >= count) continue;

                var cellX = col * cellW;
                var cellY = row * cellH;
                var baselineY = cellY + cellH / 2f;
                var traceLeft = cellX + CalAreaWidth;

                // Compare mode: each pane is an independent (pathology, lead) target rather
                // than the active rhythm's lead. Empty panes render a tappable placeholder.
                if (mode.IsCompareMode)
                {
                    DrawComparePane(ds, itemIndex, cellX, cellY, cellW, cellH, baselineY, traceLeft,
                        scale, mode, comparisonWaveforms, comparisonLabels, elapsedSeconds, textFormat, labelFormat, strokeScale);
                    continue;
                }

                if (itemIndex >= leadOrder.Count) continue;
                var lead = leadOrder[itemIndex];

                DrawCalibrationPulse(ds, cellX, baselineY, scale, palette.Trace, strokeScale);
                ds.DrawText(lead.ToString(),
                    new Rect(cellX, baselineY - 10, LabelAreaWidth, 20), palette.Trace, labelFormat);

                if (waveforms.TryGetValue(lead, out var points) && points.Values.Count >= 2)
                {
                    var traceWidth = (float)Math.Max(0, cellW - CalAreaWidth);
                    var clip = new Rect(traceLeft, cellY, traceWidth, cellH);
                    using (ds.CreateLayer(1f, clip))
                    {
                        DrawTrace(ds, points.Values, traceLeft, traceWidth, baselineY,
                            scale.PxPerSample, scale.PxPerAdcCount, scale.PxPerSec, palette.Trace,
                            mode.IsRunning, elapsedSeconds, streamSign, strokeScale);
                        // pQRSt overlay: impulse/interval labels are drawn only when the user has
                        // toggled them on (Android draws them unconditionally; here it is a button).
                        if (mode.ShowImpulseLabels && significantPoints is { Count: > 0 })
                        {
                            DrawSignificantPoints(ds, points.Values, significantPoints,
                                traceLeft, cellY, cellH, baselineY, scale, strokeScale);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Renders one comparison pane: a filled trace labelled "name (lead)", or a tappable
    /// placeholder when no target is set for this pane. Port of the Android compare-mode cell.
    /// </summary>
    private static void DrawComparePane(
        CanvasDrawingSession ds,
        int paneIndex,
        float cellX, float cellY, float cellW, float cellH,
        float baselineY, float traceLeft,
        PixelScale scale,
        MonitorModeModel mode,
        IReadOnlyDictionary<int, Points>? comparisonWaveforms,
        IReadOnlyDictionary<int, string>? comparisonLabels,
        float elapsedSeconds,
        CanvasTextFormat textFormat,
        CanvasTextFormat labelFormat,
        float strokeScale)
    {
        if (!mode.ComparisonTargets.TryGetValue(paneIndex, out var target))
        {
            DrawComparePlaceholder(ds, cellX, cellY, cellW, cellH, strokeScale);
            return;
        }

        var trace = EcgColors.Palette(mode.GridScheme, mode.BlankSheet).Trace;
        DrawCalibrationPulse(ds, cellX, baselineY, scale, trace, strokeScale);

        // Draw lead name to the left of the calibration pulse
        ds.DrawText(target.Lead.ToString(),
            new Rect(cellX, baselineY - 10, LabelAreaWidth, 20), trace, labelFormat);

        var name = comparisonLabels is not null && comparisonLabels.TryGetValue(paneIndex, out var n)
            ? n
            : target.PathologyId;
        var label = name;
        ds.DrawText(label,
            new Rect(cellX + CalAreaWidth + 4, cellY + 4, Math.Max(0, cellW - CalAreaWidth - 8), 20),
            trace, textFormat);

        if (comparisonWaveforms is not null
            && comparisonWaveforms.TryGetValue(paneIndex, out var points)
            && points.Values.Count >= 2)
        {
            var traceWidth = (float)Math.Max(0, cellW - CalAreaWidth);
            var clip = new Rect(traceLeft, cellY, traceWidth, cellH);
            using (ds.CreateLayer(1f, clip))
            {
                DrawTrace(ds, points.Values, traceLeft, traceWidth, baselineY,
                    scale.PxPerSample, scale.PxPerAdcCount, scale.PxPerSec, trace,
                    mode.IsRunning, elapsedSeconds, mode.BlankSheet ? 1f : -1f, strokeScale);
            }
        }
    }

    private static readonly Color PlaceholderFill = new() { A = 90, R = 0xB0, G = 0xB0, B = 0xB0 };
    private static readonly Color PlaceholderStroke = new() { A = 160, R = 0x90, G = 0x90, B = 0x90 };
    private static readonly Color PlaceholderText = new() { A = 255, R = 0x55, G = 0x55, B = 0x55 };

    private static void DrawComparePlaceholder(CanvasDrawingSession ds, float x, float y, float w, float h, float strokeScale = 1f)
    {
        const float pad = 8f;
        var rect = new Rect(x + pad, y + pad, Math.Max(0, w - 2 * pad), Math.Max(0, h - 2 * pad));
        ds.FillRoundedRectangle(rect, 8f, 8f, PlaceholderFill);
        ds.DrawRoundedRectangle(rect, 8f, 8f, PlaceholderStroke, 1f * strokeScale);
        using var tf = new CanvasTextFormat
        {
            FontSize = 14f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        ds.DrawText(AppStrings.ComparePlaceholder, rect, PlaceholderText, tf);
    }

    /// <summary>Inverse of the lead/compare grid layout: maps a point to its pane index, or -1.</summary>
    public static int PaneIndexAt(float width, float height, MonitorModeModel mode, double x, double y)
    {
        var count = mode.Count;
        if (count <= 0) return -1;
        var maxColumns = mode.SeriesScheme.MaxColumns();
        var rows = (int)Math.Ceiling(count / (float)maxColumns);
        if (rows <= 0) return -1;
        var columns = (int)Math.Ceiling(count / (float)rows);
        var cellW = width / columns;
        var cellH = height / rows;
        if (cellW <= 0 || cellH <= 0) return -1;
        var col = (int)(x / cellW);
        var row = (int)(y / cellH);
        if (col < 0 || col >= columns || row < 0 || row >= rows) return -1;
        var itemIndex = col * rows + row;
        return itemIndex >= count ? -1 : itemIndex;
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
        MonitorModeModel mode,
        IReadOnlyList<SignificantPoint>? significantPoints = null,
        int? selectedIndex = null,
        PhotoTransform? imageTransform = null,
        CanvasBitmap? referenceImage = null,
        int[]? ghostTrace = null,
        float viewZoom = 1f,
        float viewOffsetX = 0f,
        float viewOffsetY = 0f)
    {
        var scale = new PixelScale(PxPerMm(mode.DisplayScale), mode.Speed, 1f, mode.Calibration);
        var palette = EcgColors.Palette(mode.GridScheme, mode.BlankSheet);

        // Zoom/pan as a Win2D transform (crisp at any scale); strokes are counter-scaled by 1/zoom.
        var strokeScale = viewZoom > 0f ? 1f / viewZoom : 1f;
        ds.Transform = ViewTransform(width, height, viewZoom, viewOffsetX, viewOffsetY);
        DrawGrid(ds, width, height, scale, palette, mode.BlankSheet, 0f, strokeScale);

        if (referenceImage is not null && imageTransform is not null && imageTransform.IsVisible)
        {
            var original = ds.Transform;
            var w = referenceImage.Size.Width;
            var h = referenceImage.Size.Height;
            var matrix = Matrix3x2.CreateTranslation(-(float)w / 2f, -(float)h / 2f) *
                         Matrix3x2.CreateScale(imageTransform.Scale) *
                         Matrix3x2.CreateRotation((float)(imageTransform.RotationDeg * Math.PI / 180.0)) *
                         Matrix3x2.CreateTranslation(width / 2f + imageTransform.OffsetX, height / 2f + imageTransform.OffsetY);
            // Compose with the active view transform so the underlay zooms/pans with the trace.
            ds.Transform = matrix * original;
            ds.DrawImage(referenceImage, 0, 0, new Rect(0, 0, w, h), imageTransform.Alpha);
            ds.Transform = original;
        }

        var baselineY = height / 2f;
        var traceLeft = CalAreaWidth;

        DrawCalibrationPulse(ds, 0f, baselineY, scale, palette.Trace, strokeScale);
        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = LabelFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText(stream.Lead.ToString(),
            new Rect(0, baselineY - 10, LabelAreaWidth, 20), palette.Trace, textFormat);

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
            ds.DrawGeometry(geometry, palette.Trace, TraceStroke * strokeScale, RoundStroke);

            // Auto-detect candidate trace overlay (translucent green) — port of Android ghost line.
            if (ghostTrace is { Length: >= 2 })
            {
                var ghostColor = new Color { A = 180, R = 0, G = 200, B = 0 };
                using var ghostPb = new CanvasPathBuilder(ds);
                ghostPb.BeginFigure(traceLeft, baselineY - (ghostTrace[0] - baseline) * stepY);
                for (var i = 1; i < ghostTrace.Length; i++)
                {
                    ghostPb.AddLine(traceLeft + i * stepX, baselineY - (ghostTrace[i] - baseline) * stepY);
                }
                ghostPb.EndFigure(CanvasFigureLoop.Open);
                using var ghostGeometry = CanvasGeometry.CreatePath(ghostPb);
                ds.DrawGeometry(ghostGeometry, ghostColor, 2.5f * strokeScale, RoundStroke);
            }

            // Significant-point overlay (baseline-zeroed values match the trace mapping).
            if (significantPoints is { Count: > 0 })
            {
                var values = new float[samples.Length];
                for (var i = 0; i < samples.Length; i++) values[i] = samples[i] - baseline;
                DrawSignificantPoints(ds, values, significantPoints, traceLeft, 0f, height, baselineY, scale, strokeScale);
            }

            // Selected-sample handle: red ring + cross (port of SampleHandleOverlay).
            if (selectedIndex is { } sel && sel >= 0 && sel < samples.Length)
            {
                var x = traceLeft + sel * stepX;
                var y = baselineY - (samples[sel] - baseline) * stepY;
                const float r = 5f;
                const float arm = r * 0.7f;
                var redHandle = Rgb(0xFF, 0x00, 0x00);
                ds.DrawCircle(x, y, r, redHandle, 1f * strokeScale);
                ds.DrawLine(x - arm, y, x + arm, y, redHandle, 1f * strokeScale);
                ds.DrawLine(x, y - arm, x, y + arm, redHandle, 1f * strokeScale);
            }
        }
    }

    private static void DrawGrid(
        CanvasDrawingSession ds, float width, float height, PixelScale scale, GridPalette palette, bool blankSheet, float xOffset = 0f, float strokeScale = 1f)
    {
        if (blankSheet)
        {
            // Bedside-monitor sheet: black paper, no grid; the green trace streams over it.
            ds.Clear(palette.Background);
            return;
        }

        ds.Clear(palette.Background);

        var small = scale.SmallGridStepPx;
        var large = scale.LargeGridStepPx;
        if (small <= 0) return;

        // Vertical lines scroll horizontally with the trace; horizontal lines stay put.
        var startSmall = xOffset % small;
        if (startSmall > 0) startSmall -= small;
        var startLarge = large > 0 ? xOffset % large : 0f;
        if (startLarge > 0) startLarge -= large;

        var smallStroke = SmallStroke * strokeScale;
        var largeStroke = LargeStroke * strokeScale;
        for (var x = startSmall; x <= width; x += small)
            ds.DrawLine(x, 0, x, height, palette.SmallLine, smallStroke);
        for (var y = 0f; y <= height; y += small)
            ds.DrawLine(0, y, width, y, palette.SmallLine, smallStroke);
        for (var x = startLarge; x <= width; x += large)
            ds.DrawLine(x, 0, x, height, palette.LargeLine, largeStroke);
        for (var y = 0f; y <= height; y += large)
            ds.DrawLine(0, y, width, y, palette.LargeLine, largeStroke);
    }

    private static void DrawCalibrationPulse(
        CanvasDrawingSession ds, float cellX, float baselineY, PixelScale scale, Color trace, float strokeScale = 1f)
    {
        var pulseHeight = 1f * scale.PxPerMv;
        var pulseWidth = 0.2f * scale.PxPerSec;
        // Pulse follows the lead-title strip, so the title reads to its left.
        var startX = cellX + LabelAreaWidth + 8f;
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
        ds.DrawGeometry(geometry, trace, CalStroke * strokeScale);
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
        Color trace,
        bool isRunning,
        float elapsedSeconds,
        float directionSign = -1f,
        float strokeScale = 1f)
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

        // directionSign -1 scrolls right→left (standard monitor); +1 streams left→right.
        var xOffset = isRunning ? directionSign * (float)(elapsedSeconds * pxPerSec % periodPx) : 0f;
        var iterations = (int)(traceWidth / periodPx) + 2;

        var original = ds.Transform;
        var traceStroke = TraceStroke * strokeScale;
        // i starts at -1 so a positive (left→right) offset still fills the left edge.
        // Compose the per-tile translation with the active view transform so zoom/pan still applies.
        for (var i = -1; i <= iterations; i++)
        {
            ds.Transform = Matrix3x2.CreateTranslation(xLeft + xOffset + i * periodPx, 0f) * original;
            ds.DrawGeometry(geometry, trace, traceStroke, RoundStroke);
        }
        ds.Transform = original;
    }

    /// <summary>
    /// Draws ONLY the looping waveform (no grid, calibration pulse, or label) across the whole
    /// surface, scrolling at paper speed — a faithful port of the Android <c>PreviewPane</c> used
    /// by the editor footer. <paramref name="values"/> is the baseline-zeroed waveform; loop
    /// period = max(1s of paper, the data width).
    /// </summary>
    public static void DrawPreviewTrace(
        CanvasDrawingSession ds,
        IReadOnlyList<float> values,
        float width,
        float height,
        PixelScale scale,
        Color trace,
        float elapsedSeconds)
    {
        if (values.Count < 2) return;
        var stepX = scale.PxPerSample;
        var stepY = scale.PxPerAdcCount;
        var pxPerSec = scale.PxPerSec;
        var baselineY = height / 2f;

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

        var xOffset = -(float)(elapsedSeconds * pxPerSec % periodPx);
        var iterations = (int)(width / periodPx) + 2;

        using (ds.CreateLayer(1f, new Rect(0, 0, width, height)))
        {
            var original = ds.Transform;
            for (var i = 0; i <= iterations; i++)
            {
                ds.Transform = Matrix3x2.CreateTranslation(xOffset + i * periodPx, 0f);
                ds.DrawGeometry(geometry, trace, TraceStroke, RoundStroke);
            }
            ds.Transform = original;
        }
    }

    /// <summary>
    /// Draws the significant-point overlay (markers, peak labels, boundary lines, and
    /// interval/segment measurements: QRS, PR, ST, P, T, QT, R-R) over a single lead cell.
    /// Faithful port of the Android <c>SignificantPointOverlay</c>. <paramref name="values"/>
    /// is the baseline-zeroed waveform; coordinates match <see cref="DrawTrace"/>. Markers are
    /// placed at absolute sample offsets (not tiled/scrolled), as in Android.
    /// </summary>
    public static void DrawSignificantPoints(
        CanvasDrawingSession ds,
        IReadOnlyList<float> values,
        IReadOnlyList<SignificantPoint> points,
        float xLeft,
        float cellTop,
        float cellHeight,
        float baselineY,
        PixelScale scale,
        float strokeScale = 1f)
    {
        if (points.Count == 0) return;
        var stepX = scale.PxPerSample;
        var stepY = scale.PxPerAdcCount;
        var sampleRate = scale.Cal.SampleRateHz;
        if (stepX <= 0 || sampleRate <= 0) return;

        var red = Rgb(0xD3, 0x2F, 0x2F);
        var blue = Rgb(0x19, 0x76, 0xD2);
        var green = Rgb(0x38, 0x8E, 0x3C);
        var purple = Rgb(0x7B, 0x1F, 0xA2);
        var orange = Rgb(0xE6, 0x4A, 0x19);
        var darkGreen = Rgb(0x2E, 0x7D, 0x32);
        var redFaint = new Color { A = 153, R = 0xD3, G = 0x2F, B = 0x2F };

        using var peakFmt = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using var intervalFmt = new CanvasTextFormat
        {
            FontFamily = "Consolas",
            FontSize = 14f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // 1. Markers, peak labels, boundary lines.
        foreach (var pt in points)
        {
            if (pt.Index < 0 || pt.Index >= values.Count) continue;
            var x = xLeft + pt.Index * stepX;
            var y = baselineY - values[pt.Index] * stepY;
            var name = pt.Type.ToString();
            var isBoundary = name.EndsWith("_START") || name.EndsWith("_END");
            if (isBoundary)
            {
                ds.DrawLine(x, cellTop, x, cellTop + cellHeight, redFaint, 1.5f * strokeScale);
            }
            else
            {
                DrawHaloLabel(ds, name.Replace("_PEAK", ""), x, y - 20f, red, peakFmt);
            }
            ds.FillCircle(x, y, 4f, red);
            ds.FillCircle(x, y, 1.5f, White);
        }

        // 2. Intervals & segments (associateBy keeps the last point of each type).
        var map = new Dictionary<EcgPointType, int>();
        foreach (var pt in points) map[pt.Type] = pt.Index;

        void DrawInterval(EcgPointType s, EcgPointType e, string label, float y, Color color, bool isBelow = false)
        {
            if (!map.TryGetValue(s, out var si) || !map.TryGetValue(e, out var ei) || si >= ei) return;
            var x1 = xLeft + si * stepX;
            var x2 = xLeft + ei * stepX;
            var duration = (ei - si) / sampleRate;
            const float bracket = 8f;
            ds.DrawLine(x1, y, x2, y, color, 3f * strokeScale);
            ds.DrawLine(x1, y - bracket, x1, y + bracket, color, 3f * strokeScale);
            ds.DrawLine(x2, y - bracket, x2, y + bracket, color, 3f * strokeScale);
            var textY = isBelow ? y + 19f : y - 12f;
            DrawHaloLabel(ds, $"{label} {duration:0.000}s", (x1 + x2) / 2f, textY, color, intervalFmt);
        }

        var qrsY = map.TryGetValue(EcgPointType.R_PEAK, out var rIdx) && rIdx >= 0 && rIdx < values.Count
            ? baselineY - values[rIdx] * stepY - 40f
            : cellTop + 40f;
        DrawInterval(EcgPointType.QRS_START, EcgPointType.QRS_END, AppStrings.EcgIntervalQrs, qrsY, red);

        DrawInterval(EcgPointType.P_END, EcgPointType.QRS_START, AppStrings.EcgIntervalPr, baselineY - 40f, green);
        DrawInterval(EcgPointType.QRS_END, EcgPointType.T_START, AppStrings.EcgIntervalSt, baselineY - 40f, purple);

        var pY = map.TryGetValue(EcgPointType.P_PEAK, out var pIdx) && pIdx >= 0 && pIdx < values.Count
            ? baselineY - values[pIdx] * stepY - 30f : baselineY - 60f;
        DrawInterval(EcgPointType.P_START, EcgPointType.P_END, AppStrings.EcgIntervalP, pY, blue);

        var tY = map.TryGetValue(EcgPointType.T_PEAK, out var tIdx) && tIdx >= 0 && tIdx < values.Count
            ? baselineY - values[tIdx] * stepY - 30f : baselineY - 60f;
        DrawInterval(EcgPointType.T_START, EcgPointType.T_END, AppStrings.EcgIntervalT, tY, blue);

        DrawInterval(EcgPointType.P_START, EcgPointType.QRS_START, AppStrings.EcgIntervalPr, baselineY + 60f, orange, isBelow: true);
        DrawInterval(EcgPointType.QRS_START, EcgPointType.T_END, AppStrings.EcgIntervalQt, baselineY + 100f, blue, isBelow: true);

        // 3. R-R intervals between consecutive R peaks (drawn at the top of the cell).
        var rPeaks = points.Where(p => p.Type == EcgPointType.R_PEAK)
            .Select(p => p.Index).Where(i => i >= 0 && i < values.Count).OrderBy(i => i).ToList();
        for (var i = 0; i + 1 < rPeaks.Count; i++)
        {
            var x1 = xLeft + rPeaks[i] * stepX;
            var x2 = xLeft + rPeaks[i + 1] * stepX;
            var duration = (rPeaks[i + 1] - rPeaks[i]) / sampleRate;
            var y = cellTop + 30f;
            const float bracket = 8f;
            ds.DrawLine(x1, y, x2, y, darkGreen, 3f * strokeScale);
            ds.DrawLine(x1, y - bracket, x1, y + bracket, darkGreen, 3f * strokeScale);
            ds.DrawLine(x2, y - bracket, x2, y + bracket, darkGreen, 3f * strokeScale);
            DrawHaloLabel(ds, AppStrings.EcgRrValueFormat(duration), (x1 + x2) / 2f, y + 19f, darkGreen, intervalFmt);
        }
    }

    /// <summary>Centered text with a 1px white halo (emulates Android's <c>setShadowLayer</c>).</summary>
    private static void DrawHaloLabel(
        CanvasDrawingSession ds, string text, float cx, float cy, Color color, CanvasTextFormat fmt)
    {
        var rect = new Rect(cx - 120, cy - 20, 240, 40);
        foreach (var (dx, dy) in HaloOffsets)
            ds.DrawText(text, new Rect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height), White, fmt);
        ds.DrawText(text, rect, color, fmt);
    }

    private static readonly (float dx, float dy)[] HaloOffsets = { (-1f, 0f), (1f, 0f), (0f, -1f), (0f, 1f) };
    private static readonly Color White = new() { A = 255, R = 255, G = 255, B = 255 };
    private static Color Rgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };

}
