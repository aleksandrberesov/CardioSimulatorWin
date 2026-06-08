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
        Func<string, Lead?, IReadOnlyList<EcgTrace>> resolve)
    {
        var figureIndex = 0;
        return EcgTag.Replace(html, match =>
        {
            var attrs = Attr.Matches(match.Groups[1].Value)
                .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
            var pathologyId = (attrs.GetValueOrDefault("pathology") ?? string.Empty).Trim();
            var leadToken = attrs.GetValueOrDefault("lead");
            if (string.IsNullOrWhiteSpace(leadToken)) leadToken = null;
            var lead = leadToken is null ? (Lead?)null : Leads.FromToken(leadToken);
            var caption = attrs.GetValueOrDefault("caption");
            if (string.IsNullOrWhiteSpace(caption)) caption = null;

            var traces = string.IsNullOrEmpty(pathologyId)
                ? Array.Empty<EcgTrace>()
                : resolve(pathologyId, lead);
            return traces.Count == 0
                ? MissingFigure(pathologyId, leadToken)
                : FigureHtml(traces, caption, figureIndex++);
        });
    }

    /// <summary>Builds a <c>&lt;figure&gt;</c> with one stacked <c>&lt;svg&gt;</c> per trace.</summary>
    public static string FigureHtml(IReadOnlyList<EcgTrace> traces, string? caption, int figureIndex = 0)
    {
        var rows = string.Join("\n",
            traces.Select((t, i) => LeadSvg(t, $"ecg{figureIndex}-{i}")).Where(s => s.Length > 0));
        var cap = caption is null ? string.Empty : $"\n  <figcaption>{Escape(caption)}</figcaption>";
        return $"<figure class=\"ecg-figure\">\n{rows}{cap}\n</figure>";
    }

    private static string LeadSvg(EcgTrace trace, string uid)
    {
        var values = trace.Points.Values;
        if (values.Count < 2) return string.Empty;
        var widthPx = Math.Max(1f, (values.Count - 1) * PxPerSample);
        var maxAbs = values.Max(Math.Abs);
        // Half-height: enough to fit the signal, at least 5 mm, plus 2 mm padding.
        var halfPx = Math.Max(5f * PxPerMm, maxAbs * PxPerAdcCount + 2f * PxPerMm);
        var heightPx = halfPx * 2f;
        var d = PathData(values, halfPx);
        var label = trace.Lead.ToString();

        var sb = new StringBuilder();
        sb.Append("<svg class=\"ecg-lead\" xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append($"viewBox=\"0 0 {Fmt(widthPx)} {Fmt(heightPx)}\" ");
        sb.Append($"width=\"{Fmt(widthPx)}\" height=\"{Fmt(heightPx)}\" ");
        sb.Append($"preserveAspectRatio=\"xMidYMid meet\" role=\"img\" aria-label=\"ECG lead {label}\">");
        sb.Append(GridDefs(uid));
        sb.Append($"<rect width=\"{Fmt(widthPx)}\" height=\"{Fmt(heightPx)}\" fill=\"{GridBg}\"/>");
        sb.Append($"<rect width=\"{Fmt(widthPx)}\" height=\"{Fmt(heightPx)}\" fill=\"url(#{uid})\"/>");
        sb.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"{TraceColor}\" stroke-width=\"1.4\" ");
        sb.Append("stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        sb.Append($"<text x=\"6\" y=\"18\" font-family=\"serif\" font-weight=\"bold\" font-size=\"16\" fill=\"#000\">{label}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string PathData(IReadOnlyList<float> values, float baselineY)
    {
        var sb = new StringBuilder(values.Count * 8);
        sb.Append("M0 ").Append(Fmt(baselineY - values[0] * PxPerAdcCount));
        for (var i = 1; i < values.Count; i++)
        {
            sb.Append(" L").Append(Fmt(i * PxPerSample))
              .Append(' ').Append(Fmt(baselineY - values[i] * PxPerAdcCount));
        }
        return sb.ToString();
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
