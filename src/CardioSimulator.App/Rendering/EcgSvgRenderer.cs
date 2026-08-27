using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CardioSimulator.App.Controls;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Rendering;

/// <summary>One lead's baseline-zeroed samples, ready to draw as a static SVG trace.</summary>
public readonly record struct EcgTrace(Lead Lead, Points Points);

/// <summary>
/// Renders embedded ECG references (the <c>&lt;ecg&gt;</c> elements in lecture HTML) as static
/// inline SVG, reusing the monitor's projection at a fixed figure scale. Port of the Android
/// <c>EcgSvgRenderer</c>. Pure (no WinUI), so it can run off the UI thread.
/// </summary>
public static class EcgSvgRenderer
{
    /// <summary>Fixed figure scale (mm → px). Reference figures don't use the live zoom.</summary>
    public const float PxPerMm = 6f;

    /// <summary>Width of the per-cell left margin holding the calibration pulse + lead label
    /// (where the trace starts). Static figures render at a single fixed paper speed, so this stays
    /// a constant — unlike the live monitor's speed-dependent <see cref="EcgRenderer.TraceLeft"/>.</summary>
    private const float CalAreaWidth = 80f;

    /// <summary>Minimal left margin used for a bare snippet (no calibration pulse or lead label), so the
    /// trace doesn't touch the edge — e.g. for a compact ECG segment.</summary>
    private const float BareLeftPad = 6f;

    private static readonly EcgCalibration Cal = new();
    private static readonly float PxPerSec = 25f * PxPerMm;             // 25 mm/s standard paper speed
    private static readonly float PxPerSample = PxPerSec / Cal.SampleRateHz;
    private static readonly float PxPerMv = Cal.GainMmPerMv * PxPerMm;
    private static readonly float PxPerAdcCount = PxPerMv / Cal.AdcCountsPerMv;

    // Pink grid scheme — mirrors GridScheme.Pink.
    private const string GridBg = "#FFF5F5";
    private const string GridSmall = "#FDE4E4";
    private const string GridLarge = "#F9BDBD";
    private const string TraceColor = "#111111";

    // Quoted attribute values may contain '>' but not an unescaped '"'.
    private static readonly Regex EcgTag = new(
        "<ecg\\b((?:[^>\"]|\"[^\"]*\")*?)\\s*/?>(?:\\s*</ecg>)?", RegexOptions.IgnoreCase);
    private static readonly Regex EcgSegmentTag = new(
        "<ecgsegment\\b((?:[^>\"]|\"[^\"]*\")*?)\\s*/?>(?:\\s*</ecgsegment>)?", RegexOptions.IgnoreCase);
    private static readonly Regex Attr = new("([\\w-]+)\\s*=\\s*\"([^\"]*)\"");

    /// <summary>
    /// Replaces every <c>&lt;ecg …&gt;</c> element in <paramref name="html"/> with an inline-SVG
    /// figure. <paramref name="resolve"/> maps <c>(pathologyId, lead)</c> to traces (a null lead
    /// means "all leads"). Emits a placeholder figure when no data is available.
    /// </summary>
    public static string SubstituteEcgTags(
        string html,
        Func<string, Lead?, IReadOnlyList<EcgTrace>> resolve,
        string? monitorButtonLabel = null)
    {
        var figureIndex = 0;
        return EcgTag.Replace(html, match =>
        {
            var attrs = Attr.Matches(match.Groups[1].Value)
                .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
            var pathologyId = (attrs.GetValueOrDefault("pathology") ?? string.Empty).Trim();
            // The block id rides through onto the rendered <figure> so the host can address it (scroll-sync
            // and click-to-edit in the constructor); without this it would be lost during substitution.
            var id = attrs.GetValueOrDefault("id");
            // Multi-lead "leads" attribute, falling back to the legacy single "lead".
            var leadsToken = attrs.GetValueOrDefault("leads");
            if (string.IsNullOrWhiteSpace(leadsToken)) leadsToken = attrs.GetValueOrDefault("lead");
            var leads = Leads.ParseList(leadsToken);
            var scheme = SeriesSchemes.Parse(attrs.GetValueOrDefault("scheme"));
            var caption = attrs.GetValueOrDefault("caption");
            if (string.IsNullOrWhiteSpace(caption)) caption = null;
            // Optional author display size (either axis) — overrides the intrinsic px so the figure can be
            // scaled to fit the lecture layout. Mirrors the <ecgsegment> width/height.
            var widthPx = ParsePositiveInt(attrs.GetValueOrDefault("width"));
            var heightPx = ParsePositiveInt(attrs.GetValueOrDefault("height"));
            var align = EcgAligns.Parse(attrs.GetValueOrDefault("align"));

            var traces = ResolveTraces(pathologyId, leads, resolve);
            if (traces.Count == 0) return MissingFigure(pathologyId, leadsToken, id);
            // Optional display filter — the monitor's bands, applied to every lead so the figure matches the
            // filtered live trace the author previewed.
            if (EcgDisplayFilter.Build(HtmlCompiler.ParseFilterType(attrs.GetValueOrDefault("filter")), Cal.SampleRateHz) is { } fc)
                traces = traces.Select(t => new EcgTrace(t.Lead, EcgDisplayFilter.Apply(t.Points, fc.b, fc.a))).ToList();
            var button = MonitorButtonHtml(monitorButtonLabel, pathologyId, leads, scheme);
            return FigureHtml(traces, caption, scheme, figureIndex++, button, id: id, widthPx: widthPx, heightPx: heightPx, align: align);
        });
    }

    /// <summary>
    /// Replaces every <c>&lt;ecgsegment …&gt;</c> element with an inline-SVG figure showing a <b>windowed
    /// slice</b> of one lead of the pathology — <c>start</c>/<c>duration</c> in seconds — so a decorative
    /// sketch can be swapped for a real ECG snippet. Emits a placeholder when no data is available. When
    /// <paramref name="monitorButtonLabel"/> is set (course/Teaching view), each figure also gets an
    /// "open on monitor" button that opens the source pathology on the live monitor with this segment's
    /// single lead pre-selected.
    /// </summary>
    public static string SubstituteEcgSegmentTags(
        string html, Func<string, Lead?, IReadOnlyList<EcgTrace>> resolve,
        string? monitorButtonLabel = null)
    {
        var figureIndex = 0;
        return EcgSegmentTag.Replace(html, match =>
        {
            var attrs = Attr.Matches(match.Groups[1].Value)
                .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
            var pathologyId = (attrs.GetValueOrDefault("pathology") ?? string.Empty).Trim();
            var id = attrs.GetValueOrDefault("id"); // rides onto the <figure> for host addressing (see above)
            var lead = Leads.FromToken(attrs.GetValueOrDefault("lead") ?? "II") ?? Lead.II;
            var startSec = ParseSeconds(attrs.GetValueOrDefault("start"), 0);
            var durationSec = ParseSeconds(attrs.GetValueOrDefault("duration"), HtmlCompiler.DefaultSegmentSeconds);
            var caption = attrs.GetValueOrDefault("caption");
            if (string.IsNullOrWhiteSpace(caption)) caption = null;

            if (string.IsNullOrEmpty(pathologyId)) return MissingFigure(pathologyId, lead.ToString(), id);
            var traces = resolve(pathologyId, lead);
            if (traces.Count == 0 || traces[0].Points.Values.Count == 0) return MissingFigure(pathologyId, lead.ToString(), id);

            // Filter the whole lead (matching the editor preview) before windowing, so the emitted band and the
            // absolute tip sample indices line up regardless of where the window sits.
            var filterType = HtmlCompiler.ParseFilterType(attrs.GetValueOrDefault("filter"));
            var values = EcgDisplayFilter.Filter(traces[0].Points.Values, filterType, Cal.SampleRateHz);
            var startSample = Math.Clamp((int)Math.Round(startSec * Cal.SampleRateHz), 0, Math.Max(0, values.Count - 1));
            var count = Math.Clamp((int)Math.Round(durationSec * Cal.SampleRateHz), 1, values.Count - startSample);
            var windowed = new List<float>(count);
            for (var i = startSample; i < startSample + count; i++) windowed.Add(values[i]);

            var tips = TipOverlaySerializer.DecodeAttribute(attrs.GetValueOrDefault("tips"));
            var widthPx = ParsePositiveInt(attrs.GetValueOrDefault("width"));
            var heightPx = ParsePositiveInt(attrs.GetValueOrDefault("height"));
            var align = EcgAligns.Parse(attrs.GetValueOrDefault("align"));

            // A segment is a single-lead windowed slice, so "open on monitor" lands on the source pathology
            // with just this lead pre-selected (one-column) — the window and tip overlays don't carry to the
            // live monitor. A full-width filled button would dwarf the compact slice, so this is a small icon
            // button anchored (cornerAction) to the trace's top-right corner. Empty in the constructor preview
            // (no label) exactly as the full <ecg> button is.
            var button = CornerMonitorButtonHtml(monitorButtonLabel, pathologyId, new[] { lead }, SeriesScheme.OneColumn);
            return FigureHtml(new[] { new EcgTrace(lead, new Points(windowed)) }, caption,
                SeriesScheme.OneColumn, figureIndex++, actionHtml: button, uidPrefix: "ecgseg", calibrationPulse: false,
                tips: tips, tipSampleOffset: startSample, id: id, widthPx: widthPx, heightPx: heightPx, align: align,
                cornerAction: true);
        });
    }

    private static double ParseSeconds(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    /// <summary>Parses a positive-integer attribute (an explicit px size), returning null for a missing,
    /// non-numeric, or non-positive value.</summary>
    private static int? ParsePositiveInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    /// <summary>
    /// A "switch to monitor" button carrying the embed's pathology / leads / scheme as data
    /// attributes for the host bridge to read. Empty when <paramref name="label"/> is unset
    /// (e.g. the constructor preview, which has no monitor to open).
    /// </summary>
    private static string MonitorButtonHtml(string? label, string pathologyId, IReadOnlyList<Lead> leads, SeriesScheme scheme)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(pathologyId)) return string.Empty;
        const string style = "font:inherit;margin-top:8px;padding:6px 14px;border:1px solid #1976D2;" +
                             "border-radius:6px;background:#1976D2;color:#fff;cursor:pointer";
        return $"<button type=\"button\" class=\"ecg-open-monitor\" style=\"{style}\" " +
               $"data-pathology=\"{Escape(pathologyId)}\" data-leads=\"{string.Join(",", leads)}\" " +
               $"data-scheme=\"{scheme.ToToken()}\">{Escape(label)}</button>";
    }

    /// <summary>
    /// A <b>compact</b> "open on monitor" affordance for a small figure (the ECG segment): a ~26px icon
    /// button meant to be absolutely positioned in the figure's corner (see <see cref="FigureHtml"/>'s
    /// <c>cornerAction</c>), rather than the full-width filled button a full <c>&lt;ecg&gt;</c> uses which
    /// dwarfs a single-lead slice. Same <c>ecg-open-monitor</c> class + data attributes, so the host bridge
    /// reads it identically; <paramref name="label"/> becomes the tooltip / accessible name. Empty when the
    /// label or pathology is unset (e.g. the constructor preview).
    /// </summary>
    private static string CornerMonitorButtonHtml(string? label, string pathologyId, IReadOnlyList<Lead> leads, SeriesScheme scheme)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(pathologyId)) return string.Empty;
        const string style = "position:absolute;top:6px;right:6px;width:26px;height:26px;padding:0;line-height:0;" +
                             "display:inline-flex;align-items:center;justify-content:center;border:1px solid #1976D2;" +
                             "border-radius:6px;background:rgba(255,255,255,0.92);color:#1976D2;cursor:pointer";
        // Inline pulse (ECG/monitor) glyph — no icon webfont is available in the lecture WebView.
        const string icon = "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
                            "stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" +
                            "<path d=\"M3 12h4l2 5 4-10 2 5h6\"/></svg>";
        return $"<button type=\"button\" class=\"ecg-open-monitor ecg-open-monitor--corner\" style=\"{style}\" " +
               $"title=\"{Escape(label)}\" aria-label=\"{Escape(label)}\" " +
               $"data-pathology=\"{Escape(pathologyId)}\" data-leads=\"{string.Join(",", leads)}\" " +
               $"data-scheme=\"{scheme.ToToken()}\">{icon}</button>";
    }

    /// <summary>Resolves the traces for one embed: each listed lead in order, or all 12 when the
    /// list is empty (the legacy "no lead" meaning).</summary>
    private static IReadOnlyList<EcgTrace> ResolveTraces(
        string pathologyId, IReadOnlyList<Lead> leads, Func<string, Lead?, IReadOnlyList<EcgTrace>> resolve)
    {
        if (string.IsNullOrEmpty(pathologyId)) return Array.Empty<EcgTrace>();
        if (leads.Count == 0) return resolve(pathologyId, null);
        var traces = new List<EcgTrace>(leads.Count);
        foreach (var lead in leads) traces.AddRange(resolve(pathologyId, lead));
        return traces;
    }

    /// <summary>
    /// Builds a <c>&lt;figure&gt;</c> wrapping a single <c>&lt;svg&gt;</c> that draws every trace as
    /// a cell on one shared ECG grid — a static transcription of the live monitor (
    /// <see cref="EcgRenderer.Render"/>): cells are laid out column-major over the same rows/columns
    /// the <paramref name="scheme"/> implies, each with a calibration pulse, lead label, and trace.
    /// </summary>
    public static string FigureHtml(
        IReadOnlyList<EcgTrace> traces, string? caption,
        SeriesScheme scheme = SeriesScheme.OneColumn, int figureIndex = 0, string? actionHtml = null,
        string uidPrefix = "ecg", bool calibrationPulse = true,
        IReadOnlyList<TipOverlay>? tips = null, int tipSampleOffset = 0, string? id = null,
        int? widthPx = null, int? heightPx = null, EcgAlign align = EcgAlign.Left, bool cornerAction = false)
    {
        var valid = traces.Where(t => t.Points.Values.Count >= 2).ToList();
        var idAttr = string.IsNullOrEmpty(id) ? string.Empty : $" id=\"{Escape(id)}\"";
        // Horizontal placement within the parent block: text-align aligns the caption; the svg (a block) is
        // pulled across by its own auto margins (see MonitorSvg). Left is the default, so nothing is emitted.
        var figStyle = align == EcgAlign.Left ? string.Empty : $" style=\"text-align:{align.CssTextAlign()}\"";
        var cap = caption is null ? string.Empty : $"\n  <figcaption>{Escape(caption)}</figcaption>";
        var hasAction = !string.IsNullOrEmpty(actionHtml);
        if (valid.Count == 0)
        {
            // No trace to draw → a corner overlay has nothing to anchor to; place the action inline.
            var actionOnly = hasAction ? $"\n  {actionHtml}" : string.Empty;
            return $"<figure{idAttr} class=\"ecg-figure\"{figStyle}>{cap}{actionOnly}\n</figure>";
        }
        var svg = MonitorSvg(valid, scheme, $"{uidPrefix}{figureIndex}", calibrationPulse, tips, tipSampleOffset, widthPx, heightPx, align);
        if (cornerAction && hasAction)
        {
            // Overlay the action in the trace's corner (compact figures like the ECG segment): a shrink-to-fit
            // relative box wraps the svg so the absolutely-positioned button anchors to the trace itself, not
            // the full-width figure — correct under any alignment. The figStyle text-align places the box.
            var overlay = $"<span class=\"ecg-figure-overlay\" style=\"position:relative;display:inline-block;max-width:100%\">{svg}{actionHtml}</span>";
            return $"<figure{idAttr} class=\"ecg-figure\"{figStyle}>\n  {overlay}{cap}\n</figure>";
        }
        var action = hasAction ? $"\n  {actionHtml}" : string.Empty;
        return $"<figure{idAttr} class=\"ecg-figure\"{figStyle}>\n{svg}{cap}{action}\n</figure>";
    }

    /// <summary>Draws all leads as cells on a single continuous grid (the monitor look).
    /// <paramref name="calibrationPulse"/> = false renders a <b>bare</b> strip — no 1 mV pulse and no lead
    /// label, just the trace on the grid with a minimal margin (for compact snippets like an ECG segment).
    /// <paramref name="tips"/> (with <paramref name="tipSampleOffset"/> = the window's start sample) draws
    /// authored guide-line/label/point overlays on the strip.</summary>
    private static string MonitorSvg(IReadOnlyList<EcgTrace> traces, SeriesScheme scheme, string uid, bool calibrationPulse = true,
        IReadOnlyList<TipOverlay>? tips = null, int tipSampleOffset = 0, int? widthPx = null, int? heightPx = null,
        EcgAlign align = EcgAlign.Left)
    {
        var count = traces.Count;
        var maxColumns = scheme.MaxColumns();
        var rows = (int)Math.Ceiling(count / (float)maxColumns);
        var columns = (int)Math.Ceiling(count / (float)rows);

        // Uniform cell metrics so every lead sits on one shared grid. Half-height fits the loudest
        // lead (so none clips), at least 5 mm, plus 2 mm padding — as in the per-lead figure.
        var leftPad = calibrationPulse ? CalAreaWidth : BareLeftPad;
        var sampleCount = traces.Max(t => t.Points.Values.Count);
        var traceWidth = Math.Max(1f, (sampleCount - 1) * PxPerSample);
        var cellW = leftPad + traceWidth;
        var maxAbs = traces.Max(t => t.Points.Values.Max(Math.Abs));
        var halfPx = Math.Max(5f * PxPerMm, maxAbs * PxPerAdcCount + 2f * PxPerMm);
        var cellH = halfPx * 2f;
        var totalW = columns * cellW;
        var totalH = rows * cellH;

        // An explicit author size (either axis) overrides the intrinsic px. The viewBox keeps the drawing's
        // own coordinate space, so the trace/grid just scale to fill the box; with an override on either axis
        // the two axes are honoured independently ("none"), and an inline style defeats the stylesheet's
        // height:auto so a non-proportional height is respected while max-width:100% keeps it inside the pane.
        var sized = widthPx.HasValue || heightPx.HasValue;
        var boxW = widthPx ?? totalW;
        var boxH = heightPx ?? totalH;
        var par = sized ? "none" : "xMidYMid meet";
        // Combine the optional size override with the horizontal placement into one inline style. The svg is a
        // block with a definite width, so auto side-margins centre / right-align it within the figure; left is
        // the stylesheet default (margin:2px 0) and emits nothing.
        var styleProps = new List<string>();
        if (sized) { styleProps.Add($"width:{Fmt(boxW)}px"); styleProps.Add($"height:{Fmt(boxH)}px"); styleProps.Add("max-width:100%"); }
        if (align == EcgAlign.Center) { styleProps.Add("margin-left:auto"); styleProps.Add("margin-right:auto"); }
        else if (align == EcgAlign.Right) { styleProps.Add("margin-left:auto"); styleProps.Add("margin-right:0"); }
        var sizeStyle = styleProps.Count > 0 ? $" style=\"{string.Join(";", styleProps)}\"" : string.Empty;

        var sb = new StringBuilder();
        sb.Append("<svg class=\"ecg-lead\" xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append($"viewBox=\"0 0 {Fmt(totalW)} {Fmt(totalH)}\" ");
        sb.Append($"width=\"{Fmt(boxW)}\" height=\"{Fmt(boxH)}\" ");
        sb.Append($"preserveAspectRatio=\"{par}\" role=\"img\" aria-label=\"ECG\"{sizeStyle}>");
        sb.Append(GridDefs(uid));
        sb.Append($"<rect width=\"{Fmt(totalW)}\" height=\"{Fmt(totalH)}\" fill=\"{GridBg}\"/>");
        sb.Append($"<rect width=\"{Fmt(totalW)}\" height=\"{Fmt(totalH)}\" fill=\"url(#{uid})\"/>");

        for (var col = 0; col < columns; col++)
        {
            for (var row = 0; row < rows; row++)
            {
                var itemIndex = col * rows + row; // column-major, matches the monitor's LeadsGrid
                if (itemIndex >= count) continue;
                var trace = traces[itemIndex];
                var cellX = col * cellW;
                var baselineY = row * cellH + cellH / 2f;
                // Full figures carry a calibration pulse + lead label; a bare snippet is just the trace.
                if (calibrationPulse)
                {
                    var pulseRight = AppendCalibrationPulse(sb, cellX, baselineY);
                    AppendLabel(sb, trace.Lead.ToString(), pulseRight, baselineY);
                }
                AppendTrace(sb, trace.Points.Values, cellX + leftPad, baselineY);
                if (tips is { Count: > 0 })
                    DrawTipsSvg(sb, tips, cellX + leftPad, baselineY, tipSampleOffset,
                        cellX, row * cellH, cellX + cellW, (row + 1) * cellH);
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private const string TipColor = "#1565C0";

    /// <summary>
    /// Draws authored <see cref="TipOverlay"/>s onto a strip as SVG — the guide-line / label / point kinds
    /// used by the ECG-segment editor (a subset of <c>EcgRenderer.DrawTips</c>, ported from Win2D). A tip's
    /// sample is absolute in the full lead; <paramref name="sampleOffset"/> is the window's start sample, so
    /// it maps into the strip exactly where the trace does. Tips outside the cell are clipped/skipped.
    /// </summary>
    private static void DrawTipsSvg(
        StringBuilder sb, IReadOnlyList<TipOverlay> tips,
        float xLeft, float baselineY, int sampleOffset,
        float clipX0, float clipY0, float clipX1, float clipY1)
    {
        float X(float sample) => xLeft + (sample - sampleOffset) * PxPerSample;
        float Y(float adc) => baselineY - adc * PxPerAdcCount;
        bool InX(float x) => x >= clipX0 - 0.5f && x <= clipX1 + 0.5f;

        foreach (var tip in tips)
        {
            switch (tip.Kind)
            {
                case TipOverlayKind.VerticalLines:
                    foreach (var p in tip.Points)
                    {
                        var x = X(p.Sample);
                        if (!InX(x)) continue;
                        sb.Append($"<line x1=\"{Fmt(x)}\" y1=\"{Fmt(clipY0)}\" x2=\"{Fmt(x)}\" y2=\"{Fmt(clipY1)}\" ");
                        sb.Append($"stroke=\"{TipColor}\" stroke-width=\"1.4\"/>");
                    }
                    break;
                case TipOverlayKind.HorizontalLines:
                    foreach (var p in tip.Points)
                    {
                        var y = Y(p.Adc);
                        sb.Append($"<line x1=\"{Fmt(clipX0)}\" y1=\"{Fmt(y)}\" x2=\"{Fmt(clipX1)}\" y2=\"{Fmt(y)}\" ");
                        sb.Append($"stroke=\"{TipColor}\" stroke-width=\"1.4\"/>");
                    }
                    break;
                case TipOverlayKind.Label when tip.Points.Count >= 1:
                {
                    var x = X(tip.Points[0].Sample);
                    if (!InX(x)) break;
                    sb.Append($"<text x=\"{Fmt(x)}\" y=\"{Fmt(Y(tip.Points[0].Adc))}\" font-family=\"serif\" ");
                    sb.Append($"font-size=\"13\" fill=\"{TipColor}\">{Escape(tip.Text ?? "…")}</text>");
                    break;
                }
                case TipOverlayKind.Points:
                    foreach (var p in tip.Points)
                    {
                        var x = X(p.Sample);
                        if (!InX(x)) continue;
                        sb.Append($"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(Y(p.Adc))}\" r=\"4\" fill=\"{TipColor}\"/>");
                    }
                    break;
            }
        }
    }

    /// <summary>Appends the 1 mV calibration pulse at the far left of a cell and returns its
    /// right-edge x, where the lead title begins.</summary>
    private static float AppendCalibrationPulse(StringBuilder sb, float cellX, float baselineY)
    {
        var pulseHeight = 1f * PxPerMv;
        var pulseWidth = 0.2f * PxPerSec;
        // Pulse sits at the far left of the cell; the lead title reads to its right.
        var startX = cellX + 8f;
        const float wing = 4f;
        var d = $"M{Fmt(startX)} {Fmt(baselineY)}" +
                $" L{Fmt(startX + wing)} {Fmt(baselineY)}" +
                $" L{Fmt(startX + wing)} {Fmt(baselineY - pulseHeight)}" +
                $" L{Fmt(startX + wing + pulseWidth)} {Fmt(baselineY - pulseHeight)}" +
                $" L{Fmt(startX + wing + pulseWidth)} {Fmt(baselineY)}" +
                $" L{Fmt(startX + wing + pulseWidth + wing)} {Fmt(baselineY)}";
        sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"{TraceColor}\" stroke-width=\"1.4\" ");
        sb.Append("stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        return startX + wing + pulseWidth + wing;
    }

    private static void AppendTrace(StringBuilder sb, IReadOnlyList<float> values, float xLeft, float baselineY)
    {
        var d = new StringBuilder(values.Count * 8);
        d.Append('M').Append(Fmt(xLeft)).Append(' ').Append(Fmt(baselineY - values[0] * PxPerAdcCount));
        for (var i = 1; i < values.Count; i++)
        {
            d.Append(" L").Append(Fmt(xLeft + i * PxPerSample))
             .Append(' ').Append(Fmt(baselineY - values[i] * PxPerAdcCount));
        }
        sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"{TraceColor}\" stroke-width=\"1.4\" ");
        sb.Append("stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
    }

    private static void AppendLabel(StringBuilder sb, string label, float pulseRight, float baselineY)
    {
        // To the right of the calibration pulse, sitting just above the isoline — as on the monitor.
        var x = pulseRight + 4f;
        var y = baselineY - 4f;
        sb.Append($"<text x=\"{Fmt(x)}\" y=\"{Fmt(y)}\" text-anchor=\"start\" dominant-baseline=\"alphabetic\" ");
        sb.Append($"font-family=\"serif\" font-weight=\"bold\" font-size=\"14\" fill=\"{TraceColor}\">{Escape(label)}</text>");
    }

    private static string GridDefs(string uid)
    {
        var mm = Fmt(PxPerMm);
        var mm5 = Fmt(PxPerMm * 5f);
        return "<defs>" +
            $"<pattern id=\"{uid}s\" width=\"{mm}\" height=\"{mm}\" patternUnits=\"userSpaceOnUse\">" +
            $"<path d=\"M{mm} 0 L0 0 0 {mm}\" fill=\"none\" stroke=\"{GridSmall}\" stroke-width=\"0.5\"/>" +
            "</pattern>" +
            $"<pattern id=\"{uid}\" width=\"{mm5}\" height=\"{mm5}\" patternUnits=\"userSpaceOnUse\">" +
            $"<rect width=\"{mm5}\" height=\"{mm5}\" fill=\"url(#{uid}s)\"/>" +
            $"<path d=\"M{mm5} 0 L0 0 0 {mm5}\" fill=\"none\" stroke=\"{GridLarge}\" stroke-width=\"1\"/>" +
            "</pattern></defs>";
    }

    private static string MissingFigure(string pathologyId, string? leadToken, string? blockId = null)
    {
        var leadPart = leadToken is null ? string.Empty : $" (lead {Escape(leadToken)})";
        var label = string.IsNullOrEmpty(pathologyId) ? "(unspecified)" : Escape(pathologyId);
        // A missing embed is still addressable/clickable so the author can select a valid rhythm.
        var idAttr = string.IsNullOrEmpty(blockId) ? string.Empty : $" id=\"{Escape(blockId)}\"";
        return $"<figure{idAttr} class=\"ecg-figure ecg-missing\">" +
            $"<figcaption>ECG unavailable: {label}{leadPart}</figcaption></figure>";
    }

    /// <summary>0.1-px precision, locale-independent.</summary>
    private static string Fmt(float v)
    {
        var r = MathF.Round(v * 10f) / 10f;
        var asLong = (long)r;
        return r == asLong
            ? asLong.ToString(CultureInfo.InvariantCulture)
            : r.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
