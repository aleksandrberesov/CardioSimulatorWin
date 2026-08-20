using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Bi-directional compiler between a raw HTML body and a list of <see cref="HtmlBlock"/>s for the
/// visual block editor. Port of the Android <c>HtmlCompiler</c> (AngleSharp replaces Jsoup).
/// </summary>
public static class HtmlCompiler
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Parses an HTML body into a flat list of top-level blocks.</summary>
    public static IReadOnlyList<HtmlBlock> Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<HtmlBlock>();

        // A complete pasted document (the "All in one" / standalone layout) is opaque by design — see
        // Lecture.IsStandalone. Keep it as a single verbatim Raw block so the whole document (its
        // <head>, styles, and nested <body>) survives round-tripping through Compile untouched, and so
        // the visual editor can still navigate its inner DOM as a structure tree.
        if (IsFullDocument(html)) return new HtmlBlock[] { new HtmlBlock.Raw(html) };

        var doc = Parser.ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var body = doc.Body;
        if (body is null) return Array.Empty<HtmlBlock>();

        var blocks = new List<HtmlBlock>();
        foreach (var element in body.Children)
        {
            var elementId = string.IsNullOrWhiteSpace(element.Id) ? null : element.Id;
            var block = ParseElement(element);
            blocks.Add(elementId is null ? block : block with { Id = elementId });
        }
        return blocks;
    }

    private static HtmlBlock ParseElement(IElement element)
    {
        switch (element.LocalName)
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                return new HtmlBlock.Header(int.Parse(element.LocalName[1..]), element.TextContent);

            case "p":
            {
                var content = element.InnerHtml.Trim();
                if (content.Length >= 4 && content.StartsWith("$$") && content.EndsWith("$$"))
                {
                    return new HtmlBlock.KaTeX(content[2..^2].Trim(), DisplayMode: true);
                }
                // Tables are sometimes wrapped in a lone <p>.
                var nested = element.QuerySelector("table");
                if (nested is not null && element.Children.Length == 1 && element.Children[0] == nested)
                {
                    return ParseTable(nested);
                }
                return new HtmlBlock.Paragraph(content);
            }

            case "img":
                // Backward compat: read alt as caption for old HTML without <figure>.
                return new HtmlBlock.Image(element.GetAttribute("src") ?? string.Empty, element.GetAttribute("alt") ?? string.Empty);

            case "figure" when element.ClassList.Contains("img-figure"):
            {
                var imgEl = element.QuerySelector("img");
                var caption = element.QuerySelector("figcaption")?.TextContent ?? string.Empty;
                var figId = string.IsNullOrWhiteSpace(element.Id) ? null : element.Id;
                var imgBlock = new HtmlBlock.Image(imgEl?.GetAttribute("src") ?? string.Empty, caption);
                return figId is null ? imgBlock : imgBlock with { Id = figId };
            }

            case "figure" when element.ClassList.Contains("lecture-figure"):
                return new HtmlBlock.Figure(
                    element.QuerySelector(".lecture-figure-body")?.InnerHtml ?? string.Empty,
                    element.QuerySelector("figcaption")?.InnerHtml ?? string.Empty);

            // Semantic leaf tags → first-class blocks (typed editors are better than opaque HTML here).
            case "ul":
            case "ol":
                return new HtmlBlock.List(
                    element.Children.Where(c => c.LocalName == "li").Select(li => li.InnerHtml).ToList(),
                    Numbered: element.LocalName == "ol");

            case "blockquote":
                return new HtmlBlock.Quote(element.InnerHtml);

            case "hr":
                return new HtmlBlock.Divider();

            // App-authored container components (matched by their "lecture-*" class so foreign containers
            // with unknown classes stay Raw and keep their exact markup + the structure tree).
            case "div" when element.ClassList.Contains("lecture-card"):
                return new HtmlBlock.Card(
                    element.QuerySelector(".lecture-card-title")?.InnerHtml ?? string.Empty,
                    element.QuerySelector(".lecture-card-body")?.InnerHtml ?? string.Empty);

            case "div" when element.ClassList.Contains("lecture-note"):
            {
                var variant = element.ClassList
                    .FirstOrDefault(c => c.StartsWith("lecture-note-", StringComparison.Ordinal))?["lecture-note-".Length..]
                    ?? "info";
                return new HtmlBlock.Note(variant, element.InnerHtml);
            }

            case "section" when element.ClassList.Contains("lecture-section"):
                return new HtmlBlock.Section(
                    element.QuerySelector(".lecture-section-title")?.InnerHtml ?? string.Empty,
                    element.QuerySelector(".lecture-section-body")?.InnerHtml ?? string.Empty);

            case "div" when element.ClassList.Contains("lecture-container"):
                return new HtmlBlock.Container(element.InnerHtml);

            case "ecg":
            {
                // Prefer the multi-lead "leads" attribute; fall back to the legacy single "lead".
                var leadsAttr = element.GetAttribute("leads");
                if (string.IsNullOrWhiteSpace(leadsAttr)) leadsAttr = element.GetAttribute("lead");
                return new HtmlBlock.Ecg(
                    element.GetAttribute("pathology") ?? string.Empty,
                    Leads.ParseList(leadsAttr),
                    SeriesSchemes.Parse(element.GetAttribute("scheme")),
                    element.GetAttribute("caption") ?? string.Empty)
                {
                    Filter = ParseFilterType(element.GetAttribute("filter")),
                    WidthPx = ParsePositiveInt(element.GetAttribute("width")),
                    HeightPx = ParsePositiveInt(element.GetAttribute("height")),
                    Align = EcgAligns.Parse(element.GetAttribute("align")),
                };
            }

            case "ecgsegment":
                return new HtmlBlock.EcgSegment(
                    element.GetAttribute("pathology") ?? string.Empty,
                    Leads.FromToken(element.GetAttribute("lead") ?? "II") ?? Lead.II,
                    ParseSeconds(element.GetAttribute("start"), 0),
                    ParseSeconds(element.GetAttribute("duration"), DefaultSegmentSeconds),
                    element.GetAttribute("caption") ?? string.Empty)
                {
                    Tips = TipOverlaySerializer.DecodeAttribute(element.GetAttribute("tips")),
                    Filter = ParseFilterType(element.GetAttribute("filter")),
                    WidthPx = ParsePositiveInt(element.GetAttribute("width")),
                    HeightPx = ParsePositiveInt(element.GetAttribute("height")),
                    Align = EcgAligns.Parse(element.GetAttribute("align")),
                };

            case "table":
                return ParseTable(element);

            default:
            {
                var nested = element.QuerySelector("table");
                if (nested is not null && element.TextContent.Trim() == nested.TextContent.Trim())
                {
                    return ParseTable(nested);
                }
                // Unknown/nested markup (e.g. a <div> wrapper with SVG) is kept verbatim as a Raw block
                // instead of being shoehorned into a <p>, so its nested structure survives edits and the
                // visual editor can expose it as a navigable tree.
                return new HtmlBlock.Raw(element.OuterHtml);
            }
        }
    }

    private static HtmlBlock.Table ParseTable(IElement element)
    {
        var rows = element.QuerySelectorAll("tr")
            .Select(tr => (IReadOnlyList<string>)tr.QuerySelectorAll("td, th")
                .Select(c => c.InnerHtml.Trim())
                .ToList())
            .ToList();
        var id = string.IsNullOrWhiteSpace(element.Id) ? null : element.Id;
        var table = new HtmlBlock.Table(rows);
        return id is null ? table : table with { Id = id };
    }

    /// <summary>Compiles blocks back into a standards-compliant HTML body string.</summary>
    public static string Compile(IReadOnlyList<HtmlBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HtmlBlock.Header h:
                    sb.Append($"<h{h.Level} id=\"{h.Id}\">").Append(h.Text).Append($"</h{h.Level}>\n");
                    break;
                case HtmlBlock.Paragraph p:
                    sb.Append($"<p id=\"{p.Id}\">").Append(p.Html).Append("</p>\n");
                    break;
                case HtmlBlock.Raw r:
                    // Opaque/nested markup is emitted verbatim (no <p> wrapper), only stamping the block
                    // id on its root element for scroll-sync when it has none.
                    sb.Append(EnsureRootId(r.Html, r.Id)).Append('\n');
                    break;
                case HtmlBlock.Image img when string.IsNullOrWhiteSpace(img.Caption):
                    sb.Append($"<img id=\"{img.Id}\" src=\"").Append(img.Src).Append("\">\n");
                    break;
                case HtmlBlock.Image img:
                    sb.Append($"<figure id=\"{img.Id}\" class=\"img-figure\"><img src=\"").Append(img.Src)
                      .Append("\"><figcaption>").Append(img.Caption).Append("</figcaption></figure>\n");
                    break;
                case HtmlBlock.KaTeX k when k.DisplayMode:
                    sb.Append($"<p id=\"{k.Id}\">$$ ").Append(k.Expression).Append(" $$</p>\n");
                    break;
                case HtmlBlock.KaTeX k:
                    sb.Append($"<span id=\"{k.Id}\">$").Append(k.Expression).Append("$</span>\n");
                    break;
                case HtmlBlock.Ecg e:
                    sb.Append(BuildEcgTag(e)).Append('\n');
                    break;
                case HtmlBlock.EcgSegment seg:
                    sb.Append(BuildEcgSegmentTag(seg)).Append('\n');
                    break;
                // Structural components — built by HtmlComponents, then stamped with the block id
                // (EnsureRootId) so Parse reads them back as the same typed block and scroll-sync works.
                case HtmlBlock.List l:
                    sb.Append(EnsureRootId(HtmlComponents.List(l.Items, l.Numbered), l.Id)).Append('\n');
                    break;
                case HtmlBlock.Quote q:
                    sb.Append(EnsureRootId(HtmlComponents.Quote(q.Html, null), q.Id)).Append('\n');
                    break;
                case HtmlBlock.Note n:
                    sb.Append(EnsureRootId(HtmlComponents.Note(n.Variant, n.Html), n.Id)).Append('\n');
                    break;
                case HtmlBlock.Card c:
                    sb.Append(EnsureRootId(HtmlComponents.Card(c.Title, c.Html), c.Id)).Append('\n');
                    break;
                case HtmlBlock.Section s:
                    sb.Append(EnsureRootId(HtmlComponents.Section(s.Title, s.Html), s.Id)).Append('\n');
                    break;
                case HtmlBlock.Figure f:
                    sb.Append(EnsureRootId(HtmlComponents.Figure(f.Html, f.Caption), f.Id)).Append('\n');
                    break;
                case HtmlBlock.Divider:
                    sb.Append(EnsureRootId(HtmlComponents.Divider(), block.Id)).Append('\n');
                    break;
                case HtmlBlock.Container ct:
                    sb.Append(EnsureRootId(HtmlComponents.Container(ct.Html), ct.Id)).Append('\n');
                    break;
                case HtmlBlock.Table t:
                    sb.Append($"<table id=\"{t.Id}\">\n");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("  <tr>\n");
                        foreach (var cell in row) sb.Append("    <td>").Append(cell).Append("</td>\n");
                        sb.Append("  </tr>\n");
                    }
                    sb.Append("</table>\n");
                    break;
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Serializes an <see cref="HtmlBlock.Ecg"/> as an <c>&lt;ecg&gt;</c> element. Shared by
    /// <see cref="Compile"/> and by the visual editor's "insert/replace with ECG" actions so both emit
    /// identical markup (parsed back by <c>EcgSvgRenderer</c> and <see cref="Parse"/>).
    /// </summary>
    public static string BuildEcgTag(HtmlBlock.Ecg e)
    {
        var sb = new StringBuilder();
        sb.Append($"<ecg id=\"{e.Id}\" pathology=\"").Append(e.Pathology).Append('"');
        if (e.Leads.Count > 0) sb.Append(" leads=\"").Append(string.Join(",", e.Leads)).Append('"');
        if (e.Scheme != SeriesScheme.OneColumn) sb.Append(" scheme=\"").Append(e.Scheme.ToToken()).Append('"');
        if (e.Filter != EcgFilterType.None) sb.Append(" filter=\"").Append(FilterToken(e.Filter)).Append('"');
        if (e.WidthPx is { } w) sb.Append(" width=\"").Append(w.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (e.HeightPx is { } h) sb.Append(" height=\"").Append(h.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (e.Align != EcgAlign.Left) sb.Append(" align=\"").Append(e.Align.ToToken()).Append('"');
        sb.Append(" caption=\"").Append(e.Caption).Append("\"></ecg>");
        return sb.ToString();
    }

    /// <summary>Default strip length (seconds) for an ECG segment when none is specified.</summary>
    public const double DefaultSegmentSeconds = 2.5;

    /// <summary>Serializes an <see cref="HtmlBlock.EcgSegment"/> as an <c>&lt;ecgsegment&gt;</c> element.</summary>
    public static string BuildEcgSegmentTag(HtmlBlock.EcgSegment s)
    {
        var sb = new StringBuilder();
        sb.Append($"<ecgsegment id=\"{s.Id}\" pathology=\"").Append(s.Pathology).Append('"');
        sb.Append(" lead=\"").Append(s.Lead).Append('"');
        sb.Append(" start=\"").Append(s.StartSec.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(" duration=\"").Append(s.DurationSec.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (s.Tips.Count > 0) sb.Append(" tips=\"").Append(TipOverlaySerializer.EncodeAttribute(s.Tips)).Append('"');
        if (s.Filter != EcgFilterType.None) sb.Append(" filter=\"").Append(FilterToken(s.Filter)).Append('"');
        if (s.WidthPx is { } w) sb.Append(" width=\"").Append(w.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (s.HeightPx is { } h) sb.Append(" height=\"").Append(h.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (s.Align != EcgAlign.Left) sb.Append(" align=\"").Append(s.Align.ToToken()).Append('"');
        sb.Append(" caption=\"").Append(s.Caption).Append("\"></ecgsegment>");
        return sb.ToString();
    }

    /// <summary>
    /// The distinct pathology ids referenced by every <c>&lt;ecg&gt;</c>/<c>&lt;ecgsegment&gt;</c> embed in an
    /// HTML body, in first-seen order. Parses the DOM (so nested embeds inside cards/figures/containers and
    /// pasted standalone documents are found, not just top-level blocks) and reads each element's
    /// <c>pathology</c> attribute; blank or missing ids are skipped. Used to derive a course's rhythm list
    /// from the ECGs its lectures actually show, so the Teaching monitor's rhythm drawer is never empty.
    /// </summary>
    public static IReadOnlyList<string> ExtractEcgPathologyIds(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();

        // Use a fresh parser, NOT the shared static one: this method is called off the UI thread (the
        // course constructor derives a course's rhythm list on a background task), while the UI thread
        // parses the live preview/block editor with the shared parser. A single AngleSharp HtmlParser is
        // not safe for concurrent use, so sharing it across those two threads hangs the app. A per-call
        // instance is cheap and fully isolated.
        var doc = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in doc.QuerySelectorAll("ecg, ecgsegment"))
        {
            var pathology = element.GetAttribute("pathology")?.Trim();
            if (string.IsNullOrEmpty(pathology)) continue;
            if (seen.Add(pathology)) ids.Add(pathology);
        }
        return ids;
    }

    /// <summary>The lower-case token used for the <c>filter</c> attribute (e.g. <c>lowpass</c>), matching the
    /// <see cref="EcgFilterType"/> name. Shared with the renderer so both write/read the same spelling.</summary>
    public static string FilterToken(EcgFilterType filter) => filter.ToString().ToLowerInvariant();

    /// <summary>Parses a <c>filter</c> attribute token back to an <see cref="EcgFilterType"/> (case-insensitive),
    /// defaulting to <see cref="EcgFilterType.None"/> for a missing or unrecognized value.</summary>
    public static EcgFilterType ParseFilterType(string? raw) =>
        Enum.TryParse<EcgFilterType>(raw, ignoreCase: true, out var f) ? f : EcgFilterType.None;

    private static double ParseSeconds(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    /// <summary>Parses a positive-integer attribute (e.g. an explicit px size), returning null for a
    /// missing, non-numeric, or non-positive value.</summary>
    private static int? ParsePositiveInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    /// <summary>True when <paramref name="html"/> is a complete HTML document (starts with
    /// <c>&lt;!doctype</c> or <c>&lt;html</c>) rather than a body fragment. Mirrors the standalone
    /// detection in <c>CourseConstructorViewModel.ImportFullPage</c>.</summary>
    public static bool IsFullDocument(string html)
    {
        var trimmed = html.TrimStart();
        return trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The wrapper class for an embedded pasted page (its styles are scoped under it).</summary>
    public const string EmbedClass = "lecture-embed";

    /// <summary>
    /// Turns a complete pasted HTML document into a self-contained <b>body fragment</b> so it can live as
    /// one block alongside the app's own components in a single rendered lecture. The document's
    /// <c>&lt;style&gt;</c> rules are scoped under <see cref="EmbedClass"/> (so its page-level CSS — e.g.
    /// <c>body{display:flex}</c> — can't hijack the whole lecture), its <c>&lt;script&gt;</c>s are dropped,
    /// and its body content is wrapped in <c>&lt;div class="lecture-embed"&gt;…&lt;/div&gt;</c>. The result
    /// is not a full document, so the renderer lays it out inside the standard app template next to any
    /// header/ECG/table blocks the author adds.
    /// </summary>
    public static string EmbedDocument(string html)
    {
        var doc = Parser.ParseDocument(html);

        var scoped = new StringBuilder();
        foreach (var style in doc.QuerySelectorAll("style"))
        {
            var css = ScopeCss(style.TextContent, EmbedClass);
            if (css.Trim().Length > 0) scoped.Append(css).Append('\n');
        }
        // Viewport-height page rules would leave a full-screen gap when embedded inline — drop them.
        var scopedCss = Regex.Replace(scoped.ToString(), @"(?:min-)?height\s*:\s*[^;{}]*vh[^;{}]*;?", string.Empty,
            RegexOptions.IgnoreCase);

        var body = doc.Body;
        if (body is not null)
            foreach (var node in body.QuerySelectorAll("script, style").ToList())
                node.Remove(); // don't execute page scripts in the app document; styles are hoisted above
        var bodyInner = body?.InnerHtml ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("<div class=\"").Append(EmbedClass).Append("\">");
        if (scopedCss.Trim().Length > 0) sb.Append("<style>").Append(scopedCss.Trim()).Append("</style>");
        sb.Append(bodyInner);
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Rewrites a CSS block so every rule only applies inside <c>.{scope}</c>: page-level
    /// selectors (<c>html</c>/<c>body</c>/<c>:root</c>) become the scope element itself, <c>*</c> becomes
    /// <c>.scope *</c>, everything else is prefixed with <c>.scope </c>. <c>@media</c>/<c>@supports</c>/
    /// <c>@container</c> blocks are recursed into; <c>@keyframes</c>/<c>@font-face</c> are left intact.</summary>
    private static string ScopeCss(string css, string scope)
    {
        var scopeSel = "." + scope;
        var sb = new StringBuilder();
        int i = 0, n = css.Length;
        while (i < n)
        {
            if (i + 1 < n && css[i] == '/' && css[i + 1] == '*') // comment
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }
            var start = i;
            while (i < n && css[i] != '{' && css[i] != '}') i++;
            if (i >= n) { break; } // trailing junk (no block) — drop
            if (css[i] == '}') { i++; continue; }

            var prelude = css.Substring(start, i - start);
            // Find the matching close brace for this block.
            int depth = 0, j = i;
            for (; j < n; j++)
            {
                if (css[j] == '{') depth++;
                else if (css[j] == '}') { depth--; if (depth == 0) break; }
            }
            var inner = j < n ? css.Substring(i + 1, j - i - 1) : css.Substring(i + 1);
            i = j < n ? j + 1 : n;

            var trimmed = prelude.Trim();
            if (trimmed.StartsWith("@"))
            {
                var lower = trimmed.ToLowerInvariant();
                if (lower.StartsWith("@media") || lower.StartsWith("@supports") || lower.StartsWith("@container"))
                    sb.Append(trimmed).Append('{').Append(ScopeCss(inner, scope)).Append('}');
                else
                    sb.Append(trimmed).Append('{').Append(inner).Append('}'); // keyframes/font-face/page: verbatim
            }
            else
            {
                sb.Append(ScopeSelectorList(trimmed, scopeSel)).Append('{').Append(inner).Append('}');
            }
        }
        return sb.ToString();
    }

    private static string ScopeSelectorList(string selectors, string scopeSel) =>
        string.Join(", ", SplitTopLevel(selectors, ',').Select(s => ScopeSelector(s.Trim(), scopeSel)));

    private static string ScopeSelector(string sel, string scopeSel)
    {
        if (sel.Length == 0) return sel;
        var lead = Regex.Match(sel, @"^(html|body|:root)\b", RegexOptions.IgnoreCase);
        if (lead.Success) return scopeSel + sel.Substring(lead.Length);
        if (sel == "*") return scopeSel + " *";
        return scopeSel + " " + sel;
    }

    /// <summary>Splits on <paramref name="sep"/> at the top level only (ignoring separators inside
    /// <c>()</c> or <c>[]</c>, e.g. <c>:not(a, b)</c> or attribute selectors).</summary>
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var res = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') { if (depth > 0) depth--; }
            else if (c == sep && depth == 0) { res.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        res.Add(s.Substring(start));
        return res;
    }

    // Matches the opening tag of the first element in a fragment, capturing up to the end of the tag name.
    private static readonly Regex FirstOpenTag = new(@"<\s*[A-Za-z][\w:-]*", RegexOptions.Compiled);

    /// <summary>
    /// Stamps <paramref name="id"/> on the root element's opening tag when it has none, without
    /// re-serializing (so the author's exact nested markup is preserved). A whole standalone document is
    /// returned untouched.
    /// </summary>
    private static string EnsureRootId(string html, string id)
    {
        if (IsFullDocument(html)) return html;

        var open = FirstOpenTag.Match(html);
        if (!open.Success) return html; // bare text, no element root

        var tagEnd = html.IndexOf('>', open.Index);
        if (tagEnd < 0) return html;

        // Already carries an id within its opening tag? Leave it (Parse reads it back as the block id).
        var openingTag = html.Substring(open.Index, tagEnd - open.Index);
        if (Regex.IsMatch(openingTag, @"\sid\s*=", RegexOptions.IgnoreCase)) return html;

        return html.Insert(open.Index + open.Length, $" id=\"{id}\"");
    }
}
