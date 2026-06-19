using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

            case "ecg":
            {
                // Prefer the multi-lead "leads" attribute; fall back to the legacy single "lead".
                var leadsAttr = element.GetAttribute("leads");
                if (string.IsNullOrWhiteSpace(leadsAttr)) leadsAttr = element.GetAttribute("lead");
                return new HtmlBlock.Ecg(
                    element.GetAttribute("pathology") ?? string.Empty,
                    Leads.ParseList(leadsAttr),
                    SeriesSchemes.Parse(element.GetAttribute("scheme")),
                    element.GetAttribute("caption") ?? string.Empty);
            }

            case "table":
                return ParseTable(element);

            default:
            {
                var nested = element.QuerySelector("table");
                if (nested is not null && element.TextContent.Trim() == nested.TextContent.Trim())
                {
                    return ParseTable(nested);
                }
                return new HtmlBlock.Paragraph(element.OuterHtml);
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
                    sb.Append($"<ecg id=\"{e.Id}\" pathology=\"").Append(e.Pathology).Append('"');
                    if (e.Leads.Count > 0) sb.Append(" leads=\"").Append(string.Join(",", e.Leads)).Append('"');
                    if (e.Scheme != SeriesScheme.OneColumn) sb.Append(" scheme=\"").Append(e.Scheme.ToToken()).Append('"');
                    sb.Append(" caption=\"").Append(e.Caption).Append("\"></ecg>\n");
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
}
