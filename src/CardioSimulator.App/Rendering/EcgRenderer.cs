using System.Linq;
using System.Numerics;
using CardioSimulator.App.Controls;
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
    // ── Per-cell left-margin layout (calibration pulse + lead title), left → right ──
    //   [LeadIn][pulse: wing|plateau|wing][TitleGap][TitleClearance][title→trace gap] → trace
    // The pulse plateau and the title→trace gap are expressed in paper *time* (× PxPerSec), so
    // they scale with paper speed; everything else is a fixed screen size. This keeps the title
    // from colliding with the trace at high speed (wide pulse) or floating far from it at low
    // speed (narrow pulse) — see <see cref="TraceLeft"/>.
    // The title is drawn TitleArea wide but only TitleClearance is reserved before the trace, and
    // it floats TitleLift above the isoline: short labels (I, II, V1…) no longer leave a big empty
    // gap, and the trace starts close to the pulse while the title sits up and out of its way.
    private const float LeadIn = 8f;               // cell-left → pulse
    private const float PulseWing = 4f;            // each pulse foot
    private const float PulseSeconds = 0.2f;       // pulse plateau width, in paper time
    private const float TitleGap = 4f;             // pulse → lead title
    private const float TitleArea = 32f;           // drawn lead-title width (fits aVR/aVL/aVF @ 14)
    private const float TitleClearance = 18f;      // horizontal room kept clear before the trace
    private const float TitleLift = 10f;           // px the title floats above the isoline
    private const float TraceGapBase = 3f;         // minimum lead title → trace gap
    private const float TraceGapSeconds = 0.05f;   // additional title → trace gap, in paper time
    private const float LabelFontSize = 14f;

    /// <summary>X offset (from a cell's left edge) where the trace starts: past the calibration
    /// pulse, the title clearance, and a speed-proportional gap. The pulse plateau and the
    /// title→trace gap scale with paper speed (via <see cref="PixelScale.PxPerSec"/>), so the gap
    /// after the title grows/shrinks with speed instead of being a fixed pixel distance. Only
    /// <see cref="TitleClearance"/> (not the full drawn <see cref="TitleArea"/>) is reserved, so
    /// the trace sits close to the pulse and the lifted title may overlap its leading edge. This is
    /// the single trace-start origin shared by the draw path, the editor's pixel↔sample hit-testing
    /// (<c>EditableLeadControl</c>), and the image <c>TraceExtractor</c>.</summary>
    public static float TraceLeft(PixelScale scale) =>
        LeadIn + 2f * PulseWing + PulseSeconds * scale.PxPerSec
        + TitleGap + TitleClearance + TraceGapBase + TraceGapSeconds * scale.PxPerSec;
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
    /// Per-lead-count multiplier applied to <see cref="MonitorModeModel.DisplayScale"/> on the live
    /// monitor. With fewer leads each lead cell is much taller (<c>cellH = height / rows</c>), which
    /// otherwise leaves the fixed-scale trace as a small graphic in a sea of grid squares. Scaling
    /// the whole cell — grid <em>and</em> trace — up for sparse layouts makes them read as densely as
    /// the full 12-lead view. Hand-tuned by number of leads (not a formula), per design; 6+ leads
    /// use the base ×2. Only ever scales up.
    /// </summary>
    public static float DisplayScaleFactor(int leadCount) => leadCount switch
    {
        <= 1 => 6.0f,
        2 => 4.4f,
        3 => 3.2f,
        4 => 3.2f,
        5 => 2.4f,
        _ => 2.0f,
    };

    /// <summary>The effective px-per-mm for a live-monitor layout: the mode's
    /// <see cref="MonitorModeModel.DisplayScale"/> scaled up for sparse lead layouts (see
    /// <see cref="DisplayScaleFactor"/>). Shared by the draw path and the ruler hit-testing so both
    /// agree on the grid/trace scale.</summary>
    public static float EffectivePxPerMm(MonitorModeModel mode) =>
        PxPerMm(mode.DisplayScale * DisplayScaleFactor(mode.Count));

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
        float viewOffsetY = 0f,
        IReadOnlyList<TipOverlay>? tips = null,
        IReadOnlyList<string>? tipComments = null)
    {
        var scale = new PixelScale(EffectivePxPerMm(mode), mode.Speed, 1f, mode.Calibration);
        var palette = EcgColors.Palette(mode.GridScheme, mode.BlankSheet);

        // Apply zoom/pan as a Win2D transform so the geometry stays crisp at any scale, then
        // counter-scale every stroke width by 1/zoom so line thickness looks the same at all zooms.
        var strokeScale = viewZoom > 0f ? 1f / viewZoom : 1f;
        ds.Transform = ViewTransform(width, height, viewZoom, viewOffsetX, viewOffsetY);

        // Blank sheet streams the trace left→right (+1); the gridded monitor scrolls
        // right→left (-1) as on a real scope.
        var streamSign = mode.BlankSheet ? 1f : -1f;

        // Grid scrolls with the trace when running (matches Android requirement).
        var gridOffset = streamSign * (float)(elapsedSeconds * scale.PxPerSec);
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
        // Lead title reads to the right of the calibration pulse, sitting just above the isoline.
        using var labelFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = LabelFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Bottom,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        // Top-left aligned caption for the ЭОС on-trace "вектор a/b" labels.
        using var eosLabelFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = 12f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.NoWrap,
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
                var traceLeft = cellX + TraceLeft(scale);

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

                var pulseRight = DrawCalibrationPulse(ds, cellX, baselineY, scale, palette.Trace, strokeScale);
                DrawLeadTitle(ds, lead.ToString(), pulseRight, baselineY, palette.Trace, labelFormat);

                if (waveforms.TryGetValue(lead, out var points) && points.Values.Count >= 2)
                {
                    var traceWidth = (float)Math.Max(0, cellW - TraceLeft(scale));
                    var clip = new Rect(traceLeft, cellY, traceWidth, cellH);
                    using (ds.CreateLayer(1f, clip))
                    {
                        // ЭОС highlight: shade the QRS complexes of leads I/aVF the axis is measured
                        // from (drawn under the trace). Static sample→x mapping, like the pQRSt
                        // overlay — aligned with the QRS when the monitor is paused.
                        if (mode.EosHighlightSpans is { Count: > 0 } eosSpans
                            && eosSpans.TryGetValue(lead, out var leadSpans) && leadSpans.Count > 0)
                        {
                            // Caption the shaded QRS with its vector name (a on I, b on aVF), coloured
                            // to match the ЭОС window legend.
                            (string? eosLabel, Color eosColor) = lead switch
                            {
                                Lead.I => (AppStrings.MonitorEosVectorLabel("a"), EosVectorARed),
                                Lead.aVF => (AppStrings.MonitorEosVectorLabel("b"), EosVectorBGreen),
                                _ => ((string?)null, EosVectorARed),
                            };
                            DrawEosHighlight(ds, points.Values.Count, leadSpans, traceLeft, cellY, cellH,
                                scale, strokeScale, eosLabel, eosColor, eosLabelFormat);
                        }

                        DrawTrace(ds, points.Values, traceLeft, traceWidth, baselineY,
                            scale.PxPerSample, scale.PxPerAdcCount, scale.PxPerSec, palette.Trace,
                            mode.IsRunning, elapsedSeconds, streamSign, strokeScale);
                        // pQRSt overlay: the on-trace markup is drawn only when the pQRSt readout is
                        // on (ShowImpulseLabels) AND at least one of the measurement column's two
                        // checkboxes is ticked — Lines (boundary marks + interval brackets) and
                        // Values (the P/QRS/T + duration text) toggle independently, so the trace can
                        // be decluttered part-way. Otherwise the numbers live only in the translucent
                        // readout. (Android draws everything unconditionally.)
                        if (mode.ShowImpulseLabels
                            && (mode.ShowImpulseGraphLines || mode.ShowImpulseGraphValues)
                            && significantPoints is { Count: > 0 })
                        {
                            DrawSignificantPoints(ds, points.Values, significantPoints,
                                traceLeft, cellY, cellH, baselineY, scale, strokeScale,
                                mode.ShowImpulseGraphLines, mode.ShowImpulseGraphValues);
                        }
                    }
                }

                // Authored tip overlays for this lead's cell (bounded to the cell's trace region).
                if (mode.ShowTips && tips is { Count: > 0 })
                {
                    var cellTips = tips.Where(t => t.Lead == lead).ToList();
                    if (cellTips.Count > 0)
                    {
                        var traceWidth = (float)Math.Max(0, cellW - TraceLeft(scale));
                        using (ds.CreateLayer(1f, new Rect(traceLeft, cellY, traceWidth, cellH)))
                            DrawTips(ds, cellTips, traceLeft, baselineY, scale.PxPerSample, scale.PxPerAdcCount,
                                traceLeft, cellY, cellX + cellW, cellY + cellH, strokeScale);
                    }
                }
            }
        }

        // "Видим:" comments card — the authored explanations, screen-anchored top-left (single-rhythm
        // mode only, so it doesn't overlap compare panes). Gated by the Tips visibility toggle.
        if (mode.ShowTips && tipComments is { Count: > 0 } && !mode.IsCompareMode)
            DrawTipCommentsCard(ds, width, height, tipComments);
    }

    // Unified blue popup style (customer request 28-08-2026): the Подсказки comments card shares the ЭОС
    // window's translucent blue (#5B9BD5) with white text, matching the pQRSt values card.
    private static readonly Color TipCardFill = new() { A = 0xE0, R = 0x5B, G = 0x9B, B = 0xD5 };
    private static readonly Color TipCardBorder = new() { A = 0x66, R = 0xFF, G = 0xFF, B = 0xFF };
    private static readonly Color TipCardBody = new() { A = 0xFF, R = 0xFF, G = 0xFF, B = 0xFF };
    private static readonly Color TipCardHeader = new() { A = 0xFF, R = 0xFF, G = 0xFF, B = 0xFF };

    /// <summary>
    /// Draws the authored comments/explanations as a translucent "Видим:" card in the top-left,
    /// numbered 1..N. Screen-anchored (identity transform), so it doesn't zoom/pan with the trace.
    /// </summary>
    private static void DrawTipCommentsCard(CanvasDrawingSession ds, float width, float height, IReadOnlyList<string> comments)
    {
        ds.Transform = Matrix3x2.Identity;
        const float pad = 10f, margin = 10f, gap = 3f;
        var cardW = Math.Min(340f, Math.Max(160f, width * 0.42f));
        var innerW = cardW - pad * 2f;

        using var header = new CanvasTextFormat
        {
            FontFamily = "Times New Roman", FontSize = 15f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold, WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var body = new CanvasTextFormat
        {
            FontFamily = "Times New Roman", FontSize = 13f, WordWrapping = CanvasWordWrapping.WholeWord,
        };

        var layouts = new List<(CanvasTextLayout Layout, bool IsHeader)>
        {
            (new CanvasTextLayout(ds, AppStrings.MonitorTipsPreviewHeader, header, innerW, 0f), true),
        };
        for (var i = 0; i < comments.Count; i++)
            layouts.Add((new CanvasTextLayout(ds, $"{i + 1}. {comments[i]}", body, innerW, 0f), false));

        var totalH = pad * 2f - gap;
        foreach (var (layout, _) in layouts) totalH += (float)layout.LayoutBounds.Height + gap;
        totalH = Math.Min(totalH, height - margin * 2f);

        ds.FillRoundedRectangle(margin, margin, cardW, totalH, 8f, 8f, TipCardFill);
        ds.DrawRoundedRectangle(margin, margin, cardW, totalH, 8f, 8f, TipCardBorder, 1f);

        var y = margin + pad;
        foreach (var (layout, isHeader) in layouts)
        {
            ds.DrawTextLayout(layout, margin + pad, y, isHeader ? TipCardHeader : TipCardBody);
            y += (float)layout.LayoutBounds.Height + gap;
            layout.Dispose();
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
        var pulseRight = DrawCalibrationPulse(ds, cellX, baselineY, scale, trace, strokeScale);

        // Lead name reads to the right of the calibration pulse, just above the isoline.
        DrawLeadTitle(ds, target.Lead.ToString(), pulseRight, baselineY, trace, labelFormat);

        var name = comparisonLabels is not null && comparisonLabels.TryGetValue(paneIndex, out var n)
            ? n
            : target.PathologyId;
        var label = name;
        ds.DrawText(label,
            new Rect(traceLeft + 4, cellY + 4, Math.Max(0, cellW - TraceLeft(scale) - 8), 20),
            trace, textFormat);

        if (comparisonWaveforms is not null
            && comparisonWaveforms.TryGetValue(paneIndex, out var points)
            && points.Values.Count >= 2)
        {
            var traceWidth = (float)Math.Max(0, cellW - TraceLeft(scale));
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
        float viewOffsetY = 0f,
        float? timeRulerSeconds = null,
        IReadOnlyList<TipOverlay>? tips = null)
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
        var traceLeft = TraceLeft(scale);

        var pulseRight = DrawCalibrationPulse(ds, 0f, baselineY, scale, palette.Trace, strokeScale);
        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = LabelFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Bottom,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        DrawLeadTitle(ds, stream.Lead.ToString(), pulseRight, baselineY, palette.Trace, textFormat);

        var samples = stream.Samples;
        if (samples.Length < 2) return;

        var stepX = scale.PxPerSample;
        var stepY = scale.PxPerAdcCount;
        var clip = new Rect(traceLeft, 0, Math.Max(0, width - traceLeft), height);

        var values = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) values[i] = samples[i] - baseline;

        if (mode.FilterType != EcgFilterType.None)
        {
            var fs = EcgDisplayFilter.SampleRate(mode);
            values = EcgDisplayFilter.Filter(values, mode.FilterType, fs).ToArray();
        }

        using (ds.CreateLayer(1f, clip))
        {
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(traceLeft, baselineY - values[0] * stepY);
            for (var i = 1; i < values.Length; i++)
            {
                pb.AddLine(traceLeft + i * stepX, baselineY - values[i] * stepY);
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
                DrawSignificantPoints(ds, values, significantPoints, traceLeft, 0f, height, baselineY, scale, strokeScale);
            }

            // Selected-sample handle: red ring + cross (port of SampleHandleOverlay).
            if (selectedIndex is { } sel && sel >= 0 && sel < values.Length)
            {
                var x = traceLeft + sel * stepX;
                var y = baselineY - values[sel] * stepY;
                const float r = 5f;
                const float arm = r * 0.7f;
                var redHandle = Rgb(0xFF, 0x00, 0x00);
                ds.DrawCircle(x, y, r, redHandle, 1f * strokeScale);
                ds.DrawLine(x - arm, y, x + arm, y, redHandle, 1f * strokeScale);
                ds.DrawLine(x, y - arm, x, y + arm, redHandle, 1f * strokeScale);
            }
        }

        // Authored tip overlays (drawn outside the trace clip so areas/lines span the full cell, and
        // inside the active view transform so they zoom/pan with the trace).
        if (tips is { Count: > 0 })
            DrawTips(ds, tips, traceLeft, baselineY, stepX, stepY, traceLeft, 0f, width, height, strokeScale);

        // Time ruler: dashed marks + "N s" labels at each multiple of the chosen window (drawn last,
        // outside the trace clip, so it spans full height and reads over everything).
        if (timeRulerSeconds is { } rulerSec && rulerSec > 0f)
            DrawTimeRuler(ds, traceLeft, width, height, scale, samples.Length, rulerSec, strokeScale);
    }

    // ── Tip overlays (authored annotations) ─────────────────────────────────

    private static readonly Color TipStroke = new() { A = 235, R = 0x19, G = 0x76, B = 0xD2 };
    private static readonly Color TipFill = new() { A = 60, R = 0x19, G = 0x76, B = 0xD2 };
    private static readonly Color TipText = new() { A = 255, R = 0x0D, G = 0x47, B = 0xA1 };

    /// <summary>
    /// Draws authored <see cref="TipOverlay"/>s over one lead. Data-space points map to pixels exactly
    /// like the trace: <c>x = originX + sample·stepX</c>, <c>y = baselineY − amp·stepY</c> (amp is the
    /// baseline-relative amplitude). Full-span kinds (lead/graph areas, guide lines, ECG-part band) are
    /// bounded by [<paramref name="clipX0"/>..<paramref name="clipX1"/>] × [<paramref name="clipY0"/>..
    /// <paramref name="clipY1"/>] so they stay inside the lead's cell. Shared by the editable lead and
    /// each monitor-grid cell.
    /// </summary>
    private static void DrawTips(
        CanvasDrawingSession ds, IReadOnlyList<TipOverlay> tips,
        float originX, float baselineY, float stepX, float stepY,
        float clipX0, float clipY0, float clipX1, float clipY1, float strokeScale)
    {
        float X(float sample) => originX + sample * stepX;
        float Y(float amp) => baselineY - amp * stepY;
        var w = 2f * strokeScale;

        using var font = new CanvasTextFormat
        {
            FontFamily = "Times New Roman",
            FontSize = 13f,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        foreach (var tip in tips)
        {
            var pts = tip.Points;
            switch (tip.Kind)
            {
                case TipOverlayKind.Arrow when pts.Count >= 2:
                {
                    var (x1, y1, x2, y2) = (X(pts[0].Sample), Y(pts[0].Adc), X(pts[1].Sample), Y(pts[1].Adc));
                    ds.DrawLine(x1, y1, x2, y2, TipStroke, w);
                    DrawArrowHead(ds, x1, y1, x2, y2, w);
                    // Caption reads at the arrow's START (tail = pts[0]); the arrowhead (pts[1]) points from
                    // the label to the ECG feature. Right-aligned so it sits just before the tail.
                    if (!string.IsNullOrEmpty(tip.Text))
                        DrawTipLabel(ds, tip.Text!, x1 - 6, y1, font, alignRight: true);
                    break;
                }
                case TipOverlayKind.LeadArea:
                {
                    ds.FillRectangle(clipX0, clipY0, clipX1 - clipX0, clipY1 - clipY0, TipFill);
                    var label = tip.Lead?.ToString();
                    if (!string.IsNullOrEmpty(label))
                        DrawTipLabel(ds, label!, clipX0 + 8, clipY0 + 14, font);
                    break;
                }
                case TipOverlayKind.GraphArea when pts.Count >= 2:
                {
                    var (rx, ry) = (Math.Min(X(pts[0].Sample), X(pts[1].Sample)), Math.Min(Y(pts[0].Adc), Y(pts[1].Adc)));
                    var (rw, rh) = (Math.Abs(X(pts[1].Sample) - X(pts[0].Sample)), Math.Abs(Y(pts[1].Adc) - Y(pts[0].Adc)));
                    ds.FillRectangle(rx, ry, rw, rh, TipFill);
                    ds.DrawRectangle(rx, ry, rw, rh, TipStroke, w);
                    break;
                }
                case TipOverlayKind.FreeformArea when pts.Count >= 2:
                {
                    using var pb = new CanvasPathBuilder(ds);
                    pb.BeginFigure(X(pts[0].Sample), Y(pts[0].Adc));
                    for (var i = 1; i < pts.Count; i++) pb.AddLine(X(pts[i].Sample), Y(pts[i].Adc));
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var geo = CanvasGeometry.CreatePath(pb);
                    ds.FillGeometry(geo, TipFill);
                    ds.DrawGeometry(geo, TipStroke, w);
                    break;
                }
                case TipOverlayKind.EcgPart when pts.Count >= 2:
                {
                    var xa = Math.Clamp(Math.Min(X(pts[0].Sample), X(pts[1].Sample)), clipX0, clipX1);
                    var xb = Math.Clamp(Math.Max(X(pts[0].Sample), X(pts[1].Sample)), clipX0, clipX1);
                    ds.FillRectangle(xa, clipY0, xb - xa, clipY1 - clipY0, TipFill);
                    ds.DrawLine(xa, clipY0, xa, clipY1, TipStroke, w);
                    ds.DrawLine(xb, clipY0, xb, clipY1, TipStroke, w);
                    break;
                }
                case TipOverlayKind.VerticalLines:
                    // Point-to-point: a vertical segment at pts[0]'s x, spanning pts[0]..pts[1] in amplitude.
                    // Legacy single-point tips (no second endpoint) still span the whole cell height.
                    if (pts.Count >= 2)
                    {
                        var x = X(pts[0].Sample);
                        var (ya, yb) = (Y(pts[0].Adc), Y(pts[1].Adc));
                        ds.DrawLine(x, ya, x, yb, TipStroke, w);
                        DrawEndCaps(ds, tip.EndCap, x, ya, x, yb, w);
                    }
                    else foreach (var p in pts)
                    {
                        var x = X(p.Sample);
                        ds.DrawLine(x, clipY0, x, clipY1, TipStroke, w);
                        DrawEndCaps(ds, tip.EndCap, x, clipY0, x, clipY1, w);
                    }
                    break;
                case TipOverlayKind.HorizontalLines:
                    // Point-to-point: a horizontal segment at pts[0]'s amplitude, spanning pts[0]..pts[1] in x.
                    // Legacy single-point tips still span the whole cell width.
                    if (pts.Count >= 2)
                    {
                        var y = Y(pts[0].Adc);
                        var (xa, xb) = (X(pts[0].Sample), X(pts[1].Sample));
                        ds.DrawLine(xa, y, xb, y, TipStroke, w);
                        DrawEndCaps(ds, tip.EndCap, xa, y, xb, y, w);
                    }
                    else foreach (var p in pts)
                    {
                        var y = Y(p.Adc);
                        ds.DrawLine(clipX0, y, clipX1, y, TipStroke, w);
                        DrawEndCaps(ds, tip.EndCap, clipX0, y, clipX1, y, w);
                    }
                    break;
                case TipOverlayKind.Label when pts.Count >= 1:
                    DrawTipLabel(ds, tip.Text ?? "…", X(pts[0].Sample), Y(pts[0].Adc), font);
                    break;
                case TipOverlayKind.Points:
                    foreach (var p in pts)
                        ds.FillCircle(X(p.Sample), Y(p.Adc), 4f * strokeScale, TipStroke);
                    break;
            }
        }
    }

    private static void DrawArrowHead(CanvasDrawingSession ds, float x1, float y1, float x2, float y2, float w)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        var (ux, uy) = (dx / len, dy / len);
        const float head = 10f;
        var (px, py) = (-uy, ux); // perpendicular
        var bx = x2 - ux * head;
        var by = y2 - uy * head;
        ds.DrawLine(x2, y2, bx + px * head * 0.5f, by + py * head * 0.5f, TipStroke, w);
        ds.DrawLine(x2, y2, bx - px * head * 0.5f, by - py * head * 0.5f, TipStroke, w);
    }

    private static void DrawEndCaps(CanvasDrawingSession ds, TipLineEndCap cap, float x1, float y1, float x2, float y2, float w)
    {
        switch (cap)
        {
            case TipLineEndCap.Dots:
                ds.FillCircle(x1, y1, 3.5f * w, TipStroke);
                ds.FillCircle(x2, y2, 3.5f * w, TipStroke);
                break;
            case TipLineEndCap.Arrows:
                DrawArrowHead(ds, x2, y2, x1, y1, w);
                DrawArrowHead(ds, x1, y1, x2, y2, w);
                break;
        }
    }

    /// <summary>Draws a tip caption/label with no backing plate (author request — plain ink over the trace).
    /// <paramref name="alignRight"/> anchors the given <paramref name="x"/> at the label's right edge (used to
    /// sit an arrow's caption just before its tail), otherwise <paramref name="x"/> is the left edge.</summary>
    private static void DrawTipLabel(CanvasDrawingSession ds, string text, float x, float y, CanvasTextFormat font, bool alignRight = false)
    {
        using var layout = new CanvasTextLayout(ds, text, font, 0, 0);
        var b = layout.LayoutBounds;
        var left = alignRight ? (float)(x - b.Width) : x;
        ds.DrawTextLayout(layout, left, (float)(y - b.Height / 2), TipText);
    }

    private static readonly Color TimeRulerLine = new() { A = 150, R = 0x15, G = 0x65, B = 0xC0 };
    private static readonly Color TimeRulerText = new() { A = 255, R = 0x0D, G = 0x47, B = 0xA1 };

    /// <summary>
    /// Draws a time ruler over the editable trace: a dashed vertical marker + "N s" label at every
    /// multiple of <paramref name="seconds"/>, up to the recorded duration. Lets the author see where
    /// the 1/3/5/10 s marks fall and visualises the auto-detect window boundary. Coordinates match
    /// <see cref="RenderEditableLead"/> (drawn in the active view transform, so it zooms/pans along).
    /// </summary>
    private static void DrawTimeRuler(
        CanvasDrawingSession ds, float traceLeft, float width, float height,
        PixelScale scale, int sampleCount, float seconds, float strokeScale)
    {
        var sampleRate = scale.Cal.SampleRateHz;
        if (seconds <= 0f || scale.PxPerSec <= 0f || sampleRate <= 0f) return;
        var durationSec = sampleCount / sampleRate;
        var spacingPx = seconds * scale.PxPerSec;
        if (spacingPx <= 0f) return;

        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var fmt = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 11f,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        for (var k = 1; k * seconds <= durationSec + 1e-3f; k++)
        {
            var x = traceLeft + k * spacingPx;
            if (x > width) break;
            ds.DrawLine(x, 0f, x, height, TimeRulerLine, 1.2f * strokeScale, dash);
            ds.DrawText(AppStrings.EditorDetectWindowSeconds((int)Math.Round(k * seconds)),
                new Rect(x + 3, 2, 48, 16), TimeRulerText, fmt);
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

    /// <summary>Draws the 1 mV calibration pulse at the far left of a cell and returns its
    /// right-edge x, where the lead title begins.</summary>
    private static float DrawCalibrationPulse(
        CanvasDrawingSession ds, float cellX, float baselineY, PixelScale scale, Color trace, float strokeScale = 1f)
    {
        var pulseHeight = 1f * scale.PxPerMv;
        var pulseWidth = PulseSeconds * scale.PxPerSec;
        // Pulse sits at the far left of the cell; the lead title reads to its right.
        var startX = cellX + LeadIn;
        const float wing = PulseWing;

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
        return startX + wing + pulseWidth + wing;
    }

    /// <summary>Draws the lead title in its fixed-width <see cref="TitleArea"/> just to the right of
    /// the calibration pulse, floating <see cref="TitleLift"/> above the isoline. The drawn width
    /// (<see cref="TitleArea"/>) exceeds the reserved <see cref="TitleClearance"/>, so the title may
    /// overlap the trace's leading edge — being lifted up keeps it clear of the waveform. The fixed
    /// area (not stretched to the trace) is what lets the title→trace gap scale with speed — see
    /// <see cref="TraceLeft"/>. <paramref name="format"/> must be left/bottom aligned.</summary>
    private static void DrawLeadTitle(
        CanvasDrawingSession ds, string text, float pulseRight, float baselineY,
        Color color, CanvasTextFormat format)
    {
        var titleX = pulseRight + TitleGap;
        // Bottom-aligned text whose rect bottom sits TitleLift above the isoline, so the title
        // floats up and out of the trace's way.
        ds.DrawText(text, new Rect(titleX, baselineY - 16f - TitleLift, TitleArea, 16f), color, format);
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
        var xOffset = directionSign * (float)(elapsedSeconds * pxPerSec % periodPx);
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
    // Vector-label colours for the on-trace EOS highlight, matching the ЭОС window diagram:
    // vector a on lead I is red, vector b on aVF is green.
    private static readonly Color EosVectorARed = new() { A = 255, R = 0xD8, G = 0x3A, B = 0x3A };
    private static readonly Color EosVectorBGreen = new() { A = 255, R = 0x2E, G = 0x8B, B = 0x3A };
    private static readonly Color EosLabelBg = new() { A = 0xCC, R = 255, G = 255, B = 255 };

    /// <summary>
    /// Shades the given QRS spans of one lead as translucent blue bands (with edge lines), marking
    /// the segments the electrical axis is measured from, and captions the first band with its vector
    /// name. Coordinates match <see cref="DrawTrace"/>'s static sample offsets, so the bands sit on
    /// the QRS when the monitor is paused.
    /// </summary>
    private static void DrawEosHighlight(
        CanvasDrawingSession ds,
        int sampleCount,
        IReadOnlyList<EcgSpan> spans,
        float xLeft,
        float cellTop,
        float cellHeight,
        PixelScale scale,
        float strokeScale,
        string? label,
        Color labelColor,
        CanvasTextFormat labelFormat)
    {
        var stepX = scale.PxPerSample;
        if (stepX <= 0 || sampleCount <= 0) return;

        var fill = new Color { A = 0x33, R = 0x1E, G = 0x88, B = 0xE5 };
        var edge = new Color { A = 0x99, R = 0x1E, G = 0x88, B = 0xE5 };
        var firstBandX = float.NaN;
        foreach (var span in spans)
        {
            var s = Math.Clamp(span.StartSample, 0, sampleCount - 1);
            var e = Math.Clamp(span.EndSample, 0, sampleCount - 1);
            if (e <= s) continue;
            var x1 = xLeft + s * stepX;
            var x2 = xLeft + e * stepX;
            if (float.IsNaN(firstBandX)) firstBandX = x1;
            ds.FillRectangle(x1, cellTop, x2 - x1, cellHeight, fill);
            ds.DrawLine(x1, cellTop, x1, cellTop + cellHeight, edge, 1.5f * strokeScale);
            ds.DrawLine(x2, cellTop, x2, cellTop + cellHeight, edge, 1.5f * strokeScale);
        }

        // "вектор a" / "вектор b" caption above the first shaded QRS, on a translucent chip so it
        // reads over the trace. Colour matches the diagram legend (a=red on I, b=green on aVF).
        if (!string.IsNullOrEmpty(label) && !float.IsNaN(firstBandX))
        {
            using var layout = new CanvasTextLayout(ds, label, labelFormat, 0, 0);
            var b = layout.LayoutBounds;
            var lx = firstBandX + 3f;
            var ly = cellTop + 3f;
            ds.FillRectangle(lx - 2f, ly - 1f, (float)b.Width + 4f, (float)b.Height + 2f, EosLabelBg);
            ds.DrawTextLayout(layout, lx, ly, labelColor);
        }
    }

    public static void DrawSignificantPoints(
        CanvasDrawingSession ds,
        IReadOnlyList<float> values,
        IReadOnlyList<SignificantPoint> points,
        float xLeft,
        float cellTop,
        float cellHeight,
        float baselineY,
        PixelScale scale,
        float strokeScale = 1f,
        bool drawLines = true,
        bool drawValues = true)
    {
        if (points.Count == 0 || (!drawLines && !drawValues)) return;
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
                if (drawLines) ds.DrawLine(x, cellTop, x, cellTop + cellHeight, redFaint, 1.5f * strokeScale);
            }
            else
            {
                if (drawValues) DrawHaloLabel(ds, name.Replace("_PEAK", "").Replace("_POINT", ""), x, y - 20f, red, peakFmt);
            }
            if (drawLines)
            {
                ds.FillCircle(x, y, 4f, red);
                ds.FillCircle(x, y, 1.5f, White);
            }
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
            if (drawLines)
            {
                ds.DrawLine(x1, y, x2, y, color, 3f * strokeScale);
                ds.DrawLine(x1, y - bracket, x1, y + bracket, color, 3f * strokeScale);
                ds.DrawLine(x2, y - bracket, x2, y + bracket, color, 3f * strokeScale);
            }
            var textY = isBelow ? y + 19f : y - 12f;
            if (drawValues) DrawHaloLabel(ds, $"{label} {duration:0.000}s", (x1 + x2) / 2f, textY, color, intervalFmt);
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
            if (drawLines)
            {
                ds.DrawLine(x1, y, x2, y, darkGreen, 3f * strokeScale);
                ds.DrawLine(x1, y - bracket, x1, y + bracket, darkGreen, 3f * strokeScale);
                ds.DrawLine(x2, y - bracket, x2, y + bracket, darkGreen, 3f * strokeScale);
            }
            if (drawValues) DrawHaloLabel(ds, AppStrings.EcgRrValueFormat(duration), (x1 + x2) / 2f, y + 19f, darkGreen, intervalFmt);
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
