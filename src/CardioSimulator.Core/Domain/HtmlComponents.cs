using System.Collections.Generic;
using System.Text;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Builds HTML snippets for the "structural" content components an author can drop into a rich HTML block —
/// the kinds of elements the pasted pages are made of (Card, Section, List, Note, Quote, Figure, Divider),
/// beyond the typed blocks (heading/text/math/ECG/image/table). Pure and testable; mirrored on Android. The
/// matching styles live in <see cref="Css"/>, which the lecture renderer injects so these look right whether
/// they sit inside an embedded page or a plain fragment lecture. Text fields accept "simple HTML" (not
/// escaped), matching the typed-block editors.
/// </summary>
public static class HtmlComponents
{
    /// <summary>A bulleted (<c>ul</c>) or numbered (<c>ol</c>) list, one <c>li</c> per item.</summary>
    public static string List(IReadOnlyList<string> items, bool numbered)
    {
        var tag = numbered ? "ol" : "ul";
        var sb = new StringBuilder();
        sb.Append('<').Append(tag).Append(" class=\"lecture-list\">");
        foreach (var item in items) sb.Append("<li>").Append(item).Append("</li>");
        sb.Append("</").Append(tag).Append('>');
        return sb.ToString();
    }

    /// <summary>An elevated card with an optional title and a body.</summary>
    public static string Card(string? title, string body)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"lecture-card\">");
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("<div class=\"lecture-card-title\">").Append(title).Append("</div>");
        sb.Append("<div class=\"lecture-card-body\">").Append(body).Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>A titled section (a heading over a body).</summary>
    public static string Section(string? title, string body)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"lecture-section\">");
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("<h3 class=\"lecture-section-title\">").Append(title).Append("</h3>");
        sb.Append("<div class=\"lecture-section-body\">").Append(body).Append("</div></section>");
        return sb.ToString();
    }

    /// <summary>Recognized callout variants for <see cref="Note"/>.</summary>
    public static readonly IReadOnlyList<string> NoteVariants = new[] { "info", "tip", "warning", "important" };

    /// <summary>A coloured callout / note box in one of <see cref="NoteVariants"/>.</summary>
    public static string Note(string variant, string body)
    {
        var v = string.IsNullOrWhiteSpace(variant) ? "info" : variant.Trim().ToLowerInvariant();
        return $"<div class=\"lecture-note lecture-note-{v}\">{body}</div>";
    }

    /// <summary>A block quote with an optional citation.</summary>
    public static string Quote(string body, string? cite)
    {
        var sb = new StringBuilder();
        sb.Append("<blockquote class=\"lecture-quote\">").Append(body);
        if (!string.IsNullOrWhiteSpace(cite)) sb.Append("<cite>").Append(cite).Append("</cite>");
        sb.Append("</blockquote>");
        return sb.ToString();
    }

    /// <summary>A captioned figure frame (drop an image/diagram/ECG inside it later).</summary>
    public static string Figure(string body, string? caption)
    {
        var sb = new StringBuilder();
        sb.Append("<figure class=\"lecture-figure\"><div class=\"lecture-figure-body\">").Append(body).Append("</div>");
        if (!string.IsNullOrWhiteSpace(caption)) sb.Append("<figcaption>").Append(caption).Append("</figcaption>");
        sb.Append("</figure>");
        return sb.ToString();
    }

    /// <summary>A horizontal rule / divider.</summary>
    public static string Divider() => "<hr class=\"lecture-divider\">";

    /// <summary>A neutral grouping box (no styling of its own) that holds nested content.</summary>
    public static string Container(string html) => $"<div class=\"lecture-container\">{html}</div>";

    /// <summary>Styles for the structural components above. Injected into the lecture document by the
    /// renderer (both the fragment template and standalone pages) so inserted components look right.</summary>
    public const string Css = """
.lecture-card{border:1px solid #e2e6ea;border-radius:12px;padding:14px 18px;margin:12px 0;background:#fff;box-shadow:0 1px 4px rgba(0,20,40,.07)}
.lecture-card-title{font-weight:600;font-size:1.08em;margin-bottom:6px;color:#0b2b4a}
.lecture-section{margin:16px 0}
.lecture-section-title{margin:0 0 8px;font-size:1.15em;color:#0b2b4a;border-bottom:2px solid #e2e6ea;padding-bottom:4px}
.lecture-list{padding-left:1.5em;margin:8px 0;line-height:1.7}
.lecture-note{border-left:4px solid #1976d2;background:#e9f2fe;padding:10px 14px;margin:12px 0;border-radius:4px}
.lecture-note-tip{border-left-color:#2e7d32;background:#eaf5ea}
.lecture-note-warning{border-left-color:#ef6c00;background:#fff3e0}
.lecture-note-important{border-left-color:#c62828;background:#fdecea}
.lecture-quote{border-left:4px solid #b0bec5;margin:12px 0;padding:6px 16px;color:#455a64;font-style:italic}
.lecture-quote cite{display:block;margin-top:6px;font-size:.9em;color:#78909c;font-style:normal}
.lecture-figure{margin:12px 0;text-align:center}
.lecture-figure figcaption{font-size:.9em;color:#666;margin-top:6px}
.lecture-divider{border:none;border-top:1px solid #d0d0d0;margin:16px 0}
""";
}
