using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

            var traces = ResolveTraces(pathologyId, leads, resolve);
            if (traces.Count == 0) return MissingFigure(pathologyId, leadsToken, id);
            var button = MonitorButtonHtml(monitorButtonLabel, pathologyId, leads, scheme);
            return FigureHtml(traces, caption, scheme, figureIndex++, button, id: id);
        });
    }

    /// <summary>
    /// Replaces every <c>&lt;ecgsegment …&gt;</c> element with an inline-SVG figure showing a <b>windowed
    /// slice</b> of one lead of the pathology — <c>start</c>/<c>duration</c> in seconds — so a decorative
    /// sketch can be swapped for a real ECG snippet. Emits a placeholder when no data is available.
    /// </summary>
    public static string SubstituteEcgSegmentTags(
        string html, Func<string, Lead?, IReadOnlyList<EcgTrace>> resolve)
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

            var values = traces[0].Points.Values;
            var startSample = Math.Clamp((int)Math.Round(startSec * Cal.SampleRateHz), 0, Math.Max(0, values.Count - 1));
            var count = Math.Clamp((int)Math.Round(durationSec * Cal.SampleRateHz), 1, values.Count - startSample);
            var windowed = new List<float>(count);
            for (var i = startSample; i < startSample + count; i++) windowed.Add(values[i]);

            var tips = TipOverlaySerializer.DecodeAttribute(attrs.GetValueOrDefault("tips"));

            return FigureHtml(new[] { new EcgTrace(lead, new Points(windowed)) }, caption,
                SeriesScheme.OneColumn, figureIndex++, actionHtml: null, uidPrefix: "ecgseg", calibrationPulse: false,
                tips: tips, tipSampleOffset: startSample, id: id);
        });
    }

    private static double ParseSeconds(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

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
        IReadOnlyList<TipOverlay>? tips = null, int tipSampleOffset = 0, string? id = null)
    {
        var valid = traces.Where(t => t.Points.Values.Count >= 2).ToList();
        var idAttr = string.IsNullOrEmpty(id) ? string.Empty : $" id=\"{Escape(id)}\"";
        var cap = caption is null ? string.Empty : $"\n  <figcaption>{Escape(caption)}</figcaption>";
        var action = string.IsNullOrEmpty(actionHtml) ? string.Empty : $"\n  {actionHtml}";
        if (valid.Count == 0)
            return $"<figure{idAttr} class=\"ecg-figure\">{cap}{action}\n</figure>";
        return $"<figure{idAttr} class=\"ecg-figure\">\n{MonitorSvg(valid, scheme, $"{uidPrefix}{figureIndex}", calibrationPulse, tips, tipSampleOffset)}{cap}{action}\n</figure>";
    }

    /// <summary>Draws all leads as cells on a single continuous grid (the monitor look).
    /// <paramref name="calibrationPulse"/> = false renders a <b>bare</b> strip — no 1 mV pulse and no lead
    /// label, just the trace on the grid with a minimal margin (for compact snippets like an ECG segment).
    /// <paramref name="tips"/> (with <paramref name="tipSampleOffset"/> = the window's start sample) draws
    /// authored guide-line/label/point overlays on the strip.</summary>
    private static string MonitorSvg(IReadOnlyList<EcgTrace> traces, SeriesScheme scheme, string uid, bool calibrationPulse = true,
        IReadOnlyList<TipOverlay>? tips = null, int tipSampleOffset = 0)
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

        var sb = new StringBuilder();
        sb.Append("<svg class=\"ecg-lead\" xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append($"viewBox=\"0 0 {Fmt(totalW)} {Fmt(totalH)}\" ");
        sb.Append($"width=\"{Fmt(totalW)}\" height=\"{Fmt(totalH)}\" ");
        sb.Append("preserveAspectRatio=\"xMidYMid meet\" role=\"img\" aria-label=\"ECG\">");
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
