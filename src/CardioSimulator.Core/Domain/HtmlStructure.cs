using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Reads the nested DOM of an opaque <see cref="HtmlBlock.Raw"/> fragment (or a whole standalone
/// document) as a <b>component-oriented</b> outline — recognizable building blocks (headings, text,
/// figures/diagrams, lists, tables, ECGs, sections) rather than a raw tag dump — and performs
/// <b>surgical</b> edits on it (replace a single element or insert markup adjacent to one) without
/// disturbing the rest of the document. This is what lets the visual editor present pasted markup as an
/// understandable structure and swap a nested element (e.g. a decorative <c>&lt;svg&gt;</c> sketch) for
/// a real <c>&lt;ecg&gt;</c> reference.
/// </summary>
public static class HtmlStructure
{
    private static readonly HtmlParser Parser = new();

    /// <summary>The recognizable component category a DOM element maps to, for display and iconography.</summary>
    public enum HtmlNodeKind
    {
        /// <summary>A grouping element (section/card/div) whose children are shown beneath it.</summary>
        Container,
        Heading,
        Text,
        Math,
        Image,
        Ecg,
        Table,
        List,
        /// <summary>An <c>&lt;svg&gt;</c> figure/sketch — shown as one node (its shapes are not expanded).</summary>
        Diagram,
        Other,
    }

    /// <summary>
    /// One element in the component outline. <see cref="Path"/> is the sequence of child-element
    /// indices from the fragment root — the address <see cref="ReplaceElement"/> / <see cref="InsertAdjacent"/>
    /// navigate. <see cref="Label"/> is the friendly component name (e.g. "Section", "Heading 2",
    /// "Diagram (SVG)"); <see cref="Preview"/> is a short content snippet.
    /// </summary>
    public sealed record HtmlStructureNode(
        string Tag,
        string? Id,
        string? ClassName,
        HtmlNodeKind Kind,
        string Label,
        string? Preview,
        IReadOnlyList<int> Path,
        IReadOnlyList<HtmlStructureNode> Children);

    /// <summary>Tags treated as block-level structure (surfaced in the outline). Inline tags
    /// (<c>strong</c>, <c>span</c>, <c>br</c>, …) are folded into their parent's text preview.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "aside", "header", "footer", "nav", "figure", "figcaption",
        "ul", "ol", "li", "dl", "table", "blockquote", "form", "hr", "pre",
        "h1", "h2", "h3", "h4", "h5", "h6", "p", "img", "svg", "ecg", "ecgsegment",
    };

    /// <summary>Builds the component outline of <paramref name="html"/>. For a full document the outline
    /// starts at the <c>&lt;body&gt;</c>'s children (the head is preserved but not shown).</summary>
    public static IReadOnlyList<HtmlStructureNode> Outline(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<HtmlStructureNode>();
        var roots = RootsOf(ParseAny(html));
        var list = new List<HtmlStructureNode>(roots.Count);
        for (var i = 0; i < roots.Count; i++) list.Add(BuildNode(roots[i], new[] { i }));
        return list;
    }

    /// <summary>
    /// Finds the element carrying <paramref name="id"/> anywhere in <paramref name="html"/> and returns its
    /// outline node — whose <see cref="HtmlStructureNode.Path"/> addresses it for the surgical edit methods
    /// — or null if no element has that id. Unlike <see cref="Outline"/> this descends through
    /// <em>non-container</em> elements too, so a nested component (e.g. an <c>&lt;ecg&gt;</c> deep inside a
    /// card) is reachable. This is what lets the constructor's click-to-edit resolve a clicked nested block.
    /// </summary>
    public static HtmlStructureNode? NodeById(string html, string id)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrEmpty(id)) return null;
        var roots = RootsOf(ParseAny(html));
        for (var i = 0; i < roots.Count; i++)
            if (FindById(roots[i], new[] { i }, id) is { } node) return node;
        return null;
    }

    private static HtmlStructureNode? FindById(IElement el, int[] path, string id)
    {
        if (string.Equals(el.GetAttribute("id"), id, StringComparison.Ordinal)) return BuildNode(el, path);
        var kids = el.Children;
        for (var i = 0; i < kids.Length; i++)
        {
            var childPath = new int[path.Length + 1];
            Array.Copy(path, childPath, path.Length);
            childPath[^1] = i;
            if (FindById(kids[i], childPath, id) is { } found) return found;
        }
        return null;
    }

    /// <summary>Replaces the element at <paramref name="path"/> with <paramref name="replacementHtml"/>.
    /// Returns <paramref name="html"/> unchanged if the path no longer resolves.</summary>
    public static string ReplaceElement(string html, IReadOnlyList<int> path, string replacementHtml)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var doc = ParseAny(html);
        var target = Navigate(RootsOf(doc), path);
        if (target?.Parent is null) return html;
        target.OuterHtml = replacementHtml; // parses in the parent's context and swaps the node
        return Serialize(doc, html);
    }

    /// <summary>The current markup (<c>OuterHtml</c>) of the element at <paramref name="path"/>, or null if
    /// the path doesn't resolve. Used to seed an "edit this element" flow.</summary>
    public static string? GetOuterHtml(string html, IReadOnlyList<int> path)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return Navigate(RootsOf(ParseAny(html)), path)?.OuterHtml;
    }

    /// <summary>Removes the element at <paramref name="path"/> (and everything inside it). Returns
    /// <paramref name="html"/> unchanged if the path no longer resolves.</summary>
    public static string RemoveElement(string html, IReadOnlyList<int> path)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var doc = ParseAny(html);
        var target = Navigate(RootsOf(doc), path);
        if (target?.Parent is null) return html;
        target.Remove();
        return Serialize(doc, html);
    }

    /// <summary>Inserts <paramref name="fragment"/> immediately before (<paramref name="after"/> = false)
    /// or after (<paramref name="after"/> = true) the element at <paramref name="path"/>. Returns
    /// <paramref name="html"/> unchanged if the path no longer resolves.</summary>
    public static string InsertAdjacent(string html, IReadOnlyList<int> path, string fragment, bool after)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var doc = ParseAny(html);
        var target = Navigate(RootsOf(doc), path);
        if (target?.Parent is null) return html;
        target.Insert(after ? AdjacentPosition.AfterEnd : AdjacentPosition.BeforeBegin, fragment);
        return Serialize(doc, html);
    }

    /// <summary>Appends <paramref name="fragment"/> at the top level of the content (the last child of the
    /// body — i.e. inside a full document's <c>&lt;body&gt;</c>, or after a fragment's top-level nodes).
    /// Used to add a component to an otherwise-empty (or any) body.</summary>
    public static string AppendToRoot(string html, string fragment)
    {
        if (string.IsNullOrEmpty(html)) return fragment; // empty body → the fragment becomes the content
        var doc = ParseAny(html);
        var body = doc.Body;
        if (body is null) return html;
        body.Insert(AdjacentPosition.BeforeEnd, fragment);
        return Serialize(doc, html);
    }

    /// <summary>Appends <paramref name="fragment"/> as the last child of the element at
    /// <paramref name="path"/> (leaving its existing content intact). Returns <paramref name="html"/>
    /// unchanged if the path no longer resolves.</summary>
    public static string AppendChild(string html, IReadOnlyList<int> path, string fragment)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var doc = ParseAny(html);
        var target = Navigate(RootsOf(doc), path);
        if (target is null) return html;
        target.Insert(AdjacentPosition.BeforeEnd, fragment);
        return Serialize(doc, html);
    }

    // ── parsing / navigation ────────────────────────────────────────────────

    private static IDocument ParseAny(string html) =>
        HtmlCompiler.IsFullDocument(html)
            ? Parser.ParseDocument(html)
            : Parser.ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");

    /// <summary>The outline roots — always the body's element children, so a full document's head is
    /// preserved on serialize but not surfaced in the tree.</summary>
    private static IReadOnlyList<IElement> RootsOf(IDocument doc) =>
        doc.Body?.Children.ToArray() ?? Array.Empty<IElement>();

    private static IElement? Navigate(IReadOnlyList<IElement> roots, IReadOnlyList<int> path)
    {
        if (path.Count == 0 || path[0] < 0 || path[0] >= roots.Count) return null;
        var el = roots[path[0]];
        for (var i = 1; i < path.Count; i++)
        {
            var kids = el.Children;
            if (path[i] < 0 || path[i] >= kids.Length) return null;
            el = kids[path[i]];
        }
        return el;
    }

    private static HtmlStructureNode BuildNode(IElement el, int[] path)
    {
        var kind = Classify(el);
        var children = new List<HtmlStructureNode>();
        // Only containers surface their children; recognizable leaf components (svg diagram, image,
        // table, list, heading, text…) are shown as one node to keep the outline understandable. Only
        // block-level children become nodes — inline markup folds into the text preview — while the
        // element index (used by edits) stays aligned with the real DOM.
        if (kind == HtmlNodeKind.Container)
        {
            var kids = el.Children;
            for (var i = 0; i < kids.Length; i++)
            {
                if (!BlockTags.Contains(kids[i].LocalName)) continue;
                var childPath = new int[path.Length + 1];
                Array.Copy(path, childPath, path.Length);
                childPath[^1] = i;
                children.Add(BuildNode(kids[i], childPath));
            }
        }
        return new HtmlStructureNode(el.LocalName, AttrOrNull(el, "id"), AttrOrNull(el, "class"),
            kind, Label(el, kind), Preview(el, kind), path, children);
    }

    private static string Serialize(IDocument doc, string original) =>
        HtmlCompiler.IsFullDocument(original) ? doc.ToHtml() : doc.Body?.InnerHtml ?? string.Empty;

    // ── classification / labelling ──────────────────────────────────────────

    private static HtmlNodeKind Classify(IElement el) => el.LocalName switch
    {
        "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => HtmlNodeKind.Heading,
        "p" => IsDisplayMath(el) ? HtmlNodeKind.Math : HtmlNodeKind.Text,
        "img" or "figure" => HtmlNodeKind.Image,
        "ecg" or "ecgsegment" => HtmlNodeKind.Ecg,
        "table" => HtmlNodeKind.Table,
        "ul" or "ol" => HtmlNodeKind.List,
        "svg" => HtmlNodeKind.Diagram,
        _ => HasBlockChild(el) ? HtmlNodeKind.Container
           : HasText(el) ? HtmlNodeKind.Text
           : HtmlNodeKind.Other,
    };

    private static bool HasBlockChild(IElement el) => el.Children.Any(c => BlockTags.Contains(c.LocalName));

    private static bool HasText(IElement el) => el.TextContent.Trim().Length > 0;

    private static bool IsDisplayMath(IElement el)
    {
        var t = el.InnerHtml.Trim();
        return t.Length >= 4 && t.StartsWith("$$") && t.EndsWith("$$");
    }

    private static string Label(IElement el, HtmlNodeKind kind) => kind switch
    {
        HtmlNodeKind.Heading => $"Heading {el.LocalName[1]}",
        HtmlNodeKind.Math => "Math",
        HtmlNodeKind.Image => "Image",
        HtmlNodeKind.Ecg => el.LocalName == "ecgsegment" ? "ECG segment" : "ECG",
        HtmlNodeKind.Table => TableLabel(el),
        HtmlNodeKind.List => ListLabel(el),
        HtmlNodeKind.Diagram => "Diagram (SVG)",
        HtmlNodeKind.Text => TextLabel(el),
        HtmlNodeKind.Container => ContainerLabel(el),
        _ => el.LocalName,
    };

    private static string? Preview(IElement el, HtmlNodeKind kind) => kind switch
    {
        HtmlNodeKind.Heading or HtmlNodeKind.Text => TextDetail(el),
        HtmlNodeKind.Image => AttrOrNull(el, "alt") ?? DescendantAttr(el, "img", "src") ?? AttrOrNull(el, "src"),
        HtmlNodeKind.Ecg => AttrOrNull(el, "pathology"),
        HtmlNodeKind.Diagram => AttrOrNull(el, "viewBox") is { } vb ? Truncate(vb, 24) : SizeDetail(el),
        HtmlNodeKind.Math => TextDetail(el),
        _ => null,
    };

    /// <summary>A friendly label for a text-bearing element, inferred from its class keywords
    /// (Title / Subtitle / Caption / Note / Badge / Breadcrumb), defaulting to "Text".</summary>
    private static string TextLabel(IElement el)
    {
        var c = (AttrOrNull(el, "class") ?? string.Empty).ToLowerInvariant();
        if (c.Contains("subtitle")) return "Subtitle";
        if (c.Contains("title")) return "Title";
        if (c.Contains("caption")) return "Caption";
        if (c.Contains("breadcrumb")) return "Breadcrumb";
        if (c.Contains("note") || c.Contains("warn") || c.Contains("info") || c.Contains("tip") ||
            c.Contains("important") || c.Contains("callout")) return "Note";
        if (c.Contains("badge") || c.Contains("chip") || c.Contains("pill")) return "Badge";
        return "Text";
    }

    /// <summary>A friendly label for a grouping element, inferred from its class keywords
    /// (Card / Section / Figure / Header / Footer), defaulting to "Group".</summary>
    private static string ContainerLabel(IElement el)
    {
        var c = (AttrOrNull(el, "class") ?? string.Empty).ToLowerInvariant();
        if (c.Contains("lecture-embed") || c.Contains("embed")) return "Embedded page";
        if (c.Contains("card")) return "Card";
        if (c.Contains("section") || c.Contains("block")) return "Section";
        if (c.Contains("scheme") || c.Contains("figure") || c.Contains("diagram") || c.Contains("chart")) return "Figure";
        if (c.Contains("header")) return "Header";
        if (c.Contains("footer")) return "Footer";
        return el.LocalName switch
        {
            "section" => "Section",
            "header" => "Header",
            "footer" => "Footer",
            "article" => "Article",
            "aside" => "Aside",
            "nav" => "Nav",
            _ => "Group",
        };
    }

    private static string TableLabel(IElement el)
    {
        var rows = el.QuerySelectorAll("tr").Length;
        var cols = el.QuerySelector("tr")?.QuerySelectorAll("td, th").Length ?? 0;
        return rows > 0 ? $"Table {rows}×{cols}" : "Table";
    }

    private static string ListLabel(IElement el)
    {
        var items = el.Children.Count(c => string.Equals(c.LocalName, "li", StringComparison.OrdinalIgnoreCase));
        return items > 0 ? $"List · {items} items" : "List";
    }

    // ── small helpers ───────────────────────────────────────────────────────

    private static string? AttrOrNull(IElement el, string name)
    {
        var v = el.GetAttribute(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string? DescendantAttr(IElement el, string selector, string attr) =>
        el.QuerySelector(selector) is { } d ? AttrOrNull(d, attr) : null;

    private static string? SizeDetail(IElement el) =>
        AttrOrNull(el, "width") is { } w && AttrOrNull(el, "height") is { } h ? $"{w}×{h}" : null;

    private static string? TextDetail(IElement el)
    {
        var t = el.TextContent.Trim();
        return t.Length == 0 ? null : Truncate(t, 60);
    }

    private static string Truncate(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
