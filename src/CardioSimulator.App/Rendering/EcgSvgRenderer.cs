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

    /// <summary>Width of the per-cell left margin holding the calibration pulse + lead label,
    /// matching the live monitor's <see cref="EcgRenderer.CalAreaWidth"/>.</summary>
    private const float CalAreaWidth = 48f;

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
            // Multi-lead "leads" attribute, falling back to the legacy single "lead".
            var leadsToken = attrs.GetValueOrDefault("leads");
            if (string.IsNullOrWhiteSpace(leadsToken)) leadsToken = attrs.GetValueOrDefault("lead");
            var leads = Leads.ParseList(leadsToken);
            var scheme = SeriesSchemes.Parse(attrs.GetValueOrDefault("scheme"));
            var caption = attrs.GetValueOrDefault("caption");
            if (string.IsNullOrWhiteSpace(caption)) caption = null;

            var traces = ResolveTraces(pathologyId, leads, resolve);
            if (traces.Count == 0) return MissingFigure(pathologyId, leadsToken);
            var button = MonitorButtonHtml(monitorButtonLabel, pathologyId, leads, scheme);
            return FigureHtml(traces, caption, scheme, figureIndex++, button);
        });
    }

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
        SeriesScheme scheme = SeriesScheme.OneColumn, int figureIndex = 0, string? actionHtml = null)
    {
        var valid = traces.Where(t => t.Points.Values.Count >= 2).ToList();
        var cap = caption is null ? string.Empty : $"\n  <figcaption>{Escape(caption)}</figcaption>";
        var action = string.IsNullOrEmpty(actionHtml) ? string.Empty : $"\n  {actionHtml}";
        if (valid.Count == 0)
            return $"<figure class=\"ecg-figure\">{cap}{action}\n</figure>";
        return $"<figure class=\"ecg-figure\">\n{MonitorSvg(valid, scheme, $"ecg{figureIndex}")}{cap}{action}\n</figure>";
    }

    /// <summary>Draws all leads as cells on a single continuous grid (the monitor look).</summary>
    private static string MonitorSvg(IReadOnlyList<EcgTrace> traces, SeriesScheme scheme, string uid)
    {
        var count = traces.Count;
        var maxColumns = scheme.MaxColumns();
        var rows = (int)Math.Ceiling(count / (float)maxColumns);
        var columns = (int)Math.Ceiling(count / (float)rows);

        // Uniform cell metrics so every lead sits on one shared grid. Half-height fits the loudest
        // lead (so none clips), at least 5 mm, plus 2 mm padding — as in the per-lead figure.
        var sampleCount = traces.Max(t => t.Points.Values.Count);
        var traceWidth = Math.Max(1f, (sampleCount - 1) * PxPerSample);
        var cellW = CalAreaWidth + traceWidth;
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
                AppendCalibrationPulse(sb, cellX, baselineY);
                AppendTrace(sb, trace.Points.Values, cellX + CalAreaWidth, baselineY);
                AppendLabel(sb, trace.Lead.ToString(), cellX, baselineY);
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendCalibrationPulse(StringBuilder sb, float cellX, float baselineY)
    {
        var pulseHeight = 1f * PxPerMv;
        var pulseWidth = 0.2f * PxPerSec;
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

    private static void AppendLabel(StringBuilder sb, string label, float cellX, float baselineY)
    {
        // Centered in the calibration area, just below the baseline — as on the monitor.
        var x = cellX + 4f + CalAreaWidth / 2f;
        var y = baselineY + 30f;
        sb.Append($"<text x=\"{Fmt(x)}\" y=\"{Fmt(y)}\" text-anchor=\"middle\" ");
        sb.Append($"font-family=\"serif\" font-weight=\"bold\" font-size=\"16\" fill=\"#000\">{Escape(label)}</text>");
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

    private static string MissingFigure(string pathologyId, string? leadToken)
    {
        var leadPart = leadToken is null ? string.Empty : $" (lead {Escape(leadToken)})";
        var id = string.IsNullOrEmpty(pathologyId) ? "(unspecified)" : Escape(pathologyId);
        return "<figure class=\"ecg-figure ecg-missing\">" +
            $"<figcaption>ECG unavailable: {id}{leadPart}</figcaption></figure>";
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
