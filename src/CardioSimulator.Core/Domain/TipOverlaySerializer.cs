using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Bidirectional codec for a list of authored <see cref="TipOverlay"/>s. The wire format
/// (<c>kind|cap|lead|text|s:a;s:a…</c> per overlay, overlays joined by <c>~</c>, text %-escaped) is the
/// one used by the pathology <c>tips:</c> header (see <c>PathologyParser</c>, which delegates here). Also
/// provides Base64 <see cref="EncodeAttribute"/>/<see cref="DecodeAttribute"/> so the same overlays can ride
/// safely inside an HTML attribute (e.g. <c>&lt;ecgsegment tips="…"&gt;</c>) without escaping headaches.
/// </summary>
public static class TipOverlaySerializer
{
    /// <summary>Serializes overlays to the plain wire format.</summary>
    public static string Serialize(IReadOnlyList<TipOverlay> tips)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < tips.Count; i++)
        {
            if (i > 0) sb.Append('~');
            var t = tips[i];
            sb.Append(t.Kind.ToString()).Append('|')
              .Append(t.EndCap.ToString()).Append('|')
              .Append(t.Lead?.ToString() ?? string.Empty).Append('|')
              .Append(EscapeText(t.Text)).Append('|');
            for (var p = 0; p < t.Points.Count; p++)
            {
                if (p > 0) sb.Append(';');
                sb.Append(t.Points[p].Sample.ToString("0.###", CultureInfo.InvariantCulture)).Append(':')
                  .Append(t.Points[p].Adc.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>Parses the wire format. Overlays with an unknown kind or bad shape are skipped.</summary>
    public static IReadOnlyList<TipOverlay> Parse(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<TipOverlay>();
        var outList = new List<TipOverlay>();
        foreach (var chunk in field.Split('~'))
        {
            var fields = chunk.Split('|');
            if (fields.Length < 5) continue;
            if (!Enum.TryParse<TipOverlayKind>(fields[0].Trim(), out var kind)) continue;
            Enum.TryParse<TipLineEndCap>(fields[1].Trim(), out var cap);
            var lead = Leads.FromToken(fields[2]);
            var text = fields[3].Length == 0 ? null : UnescapeText(fields[3]);

            var points = new List<TipPoint>();
            if (fields[4].Length > 0)
            {
                foreach (var token in fields[4].Split(';'))
                {
                    var parts = token.Split(':');
                    if (parts.Length != 2) continue;
                    if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) continue;
                    if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) continue;
                    points.Add(new TipPoint(s, a));
                }
            }
            outList.Add(new TipOverlay(kind, points, text, lead, cap));
        }
        return outList;
    }

    /// <summary>Base64 of <see cref="Serialize"/> — safe inside an HTML attribute value (no <c>"</c>/<c>&lt;</c>/<c>&gt;</c>).
    /// Empty for an empty list.</summary>
    public static string EncodeAttribute(IReadOnlyList<TipOverlay> tips) =>
        tips.Count == 0 ? string.Empty
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(tips)));

    /// <summary>Inverse of <see cref="EncodeAttribute"/>; tolerant of malformed input.</summary>
    public static IReadOnlyList<TipOverlay> DecodeAttribute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<TipOverlay>();
        try { return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))); }
        catch { return Array.Empty<TipOverlay>(); }
    }

    public static string EscapeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("%", "%25")
            .Replace("|", "%7C")
            .Replace("~", "%7E")
            .Replace("\r", "%0D")
            .Replace("\n", "%0A");
    }

    public static string UnescapeText(string text) =>
        text
            .Replace("%0A", "\n")
            .Replace("%0D", "\r")
            .Replace("%7E", "~")
            .Replace("%7C", "|")
            .Replace("%25", "%");
}
