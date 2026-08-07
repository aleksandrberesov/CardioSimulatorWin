using CardioSimulator.Core.Domain;
using Xunit;
using Kind = CardioSimulator.Core.Domain.HtmlStructure.HtmlNodeKind;

namespace CardioSimulator.Core.Tests;

public class HtmlStructureTests
{
    private const string Nested =
        "<div class=\"scheme\">" +
          "<svg viewBox=\"0 0 700 180\">" +
            "<rect x=\"1\"/>" +
            "<path d=\"M280,85 L290,65\"/>" +
          "</svg>" +
        "</div>";

    [Fact]
    public void Outline_PresentsComponentsAndDoesNotExpandSvgInternals()
    {
        var outline = HtmlStructure.Outline(Nested);

        var div = Assert.Single(outline);
        Assert.Equal("div", div.Tag);
        Assert.Equal(Kind.Container, div.Kind);
        Assert.Equal("Figure", div.Label); // class "scheme" → Figure

        var svg = Assert.Single(div.Children);
        Assert.Equal("svg", svg.Tag);
        Assert.Equal(Kind.Diagram, svg.Kind);
        Assert.Equal("Diagram (SVG)", svg.Label);
        Assert.Empty(svg.Children);              // shapes are decluttered
        Assert.Equal(new[] { 0, 0 }, svg.Path);  // div[0] › svg[0]
    }

    [Fact]
    public void Outline_ClassifiesStandardComponents()
    {
        const string html =
            "<div class=\"card\">" +
              "<h2>Title</h2>" +
              "<p class=\"desc-text\">Some text</p>" +
              "<ul><li>a</li><li>b</li></ul>" +
              "<table><tr><td>x</td><td>y</td></tr></table>" +
              "<ecg pathology=\"sinus\"></ecg>" +
              "<div class=\"note-box\">Important note</div>" +
            "</div>";

        var card = Assert.Single(HtmlStructure.Outline(html));
        Assert.Equal(Kind.Container, card.Kind);
        Assert.Equal("Card", card.Label);

        var kids = card.Children;
        Assert.Equal(Kind.Heading, kids[0].Kind);
        Assert.Equal("Heading 2", kids[0].Label);
        Assert.Equal(Kind.Text, kids[1].Kind);
        Assert.Equal(Kind.List, kids[2].Kind);
        Assert.Equal("List · 2 items", kids[2].Label);
        Assert.Equal(Kind.Table, kids[3].Kind);
        Assert.Equal("Table 1×2", kids[3].Label);
        Assert.Equal(Kind.Ecg, kids[4].Kind);
        Assert.Equal(Kind.Text, kids[5].Kind);
        Assert.Equal("Note", kids[5].Label); // class "note-box" → Note
    }

    [Fact]
    public void ReplaceElement_ReplacesTheWholeSvgSketch()
    {
        var svg = HtmlStructure.Outline(Nested)[0].Children[0];

        var result = HtmlStructure.ReplaceElement(Nested, svg.Path, "<ecg pathology=\"sinus\"></ecg>");

        Assert.Contains("pathology=\"sinus\"", result);
        Assert.DoesNotContain("<svg", result);
        Assert.DoesNotContain("<path", result);
        Assert.Contains("class=\"scheme\"", result); // wrapper div preserved
    }

    [Fact]
    public void ReplaceElement_CanStillTargetAnInnerShapeByExplicitPath()
    {
        // The outline hides shapes, but the surgical edit still works for any valid DOM path.
        var result = HtmlStructure.ReplaceElement(Nested, new[] { 0, 0, 1 }, "<ecg pathology=\"x\"></ecg>");

        Assert.Contains("pathology=\"x\"", result);
        Assert.DoesNotContain("M280,85", result); // targeted path gone
        Assert.Contains("<rect", result);         // sibling preserved
    }

    [Fact]
    public void InsertAdjacent_PlacesTagAfterTheTarget()
    {
        var svg = HtmlStructure.Outline(Nested)[0].Children[0];

        var result = HtmlStructure.InsertAdjacent(Nested, svg.Path, "<ecg pathology=\"a\"></ecg>", after: true);

        Assert.Contains("<svg", result); // original kept
        Assert.Contains("pathology=\"a\"", result);
        Assert.True(result.IndexOf("<svg") < result.IndexOf("pathology=\"a\""));
    }

    [Fact]
    public void InsertAdjacent_PlacesTagBeforeTheTarget()
    {
        var svg = HtmlStructure.Outline(Nested)[0].Children[0];

        var result = HtmlStructure.InsertAdjacent(Nested, svg.Path, "<ecg pathology=\"b\"></ecg>", after: false);

        Assert.Contains("<svg", result);
        Assert.True(result.IndexOf("pathology=\"b\"") < result.IndexOf("<svg"));
    }

    [Fact]
    public void ReplaceElement_InFullDocument_PreservesHeadAndStyles()
    {
        const string doc = "<!DOCTYPE html><html><head><style>.x{color:red}</style></head><body>" +
            "<div><svg><path d=\"M1,1\"/></svg></div></body></html>";

        var svg = HtmlStructure.Outline(doc)[0].Children[0]; // div[0] › svg[0]
        Assert.Equal(Kind.Diagram, svg.Kind);

        var result = HtmlStructure.ReplaceElement(doc, svg.Path, "<ecg pathology=\"z\"></ecg>");

        Assert.Contains("<style>.x{color:red}</style>", result); // <head> preserved
        Assert.Contains("pathology=\"z\"", result);
        Assert.DoesNotContain("<svg", result);
        // Must stay a full document so it keeps round-tripping as a single standalone Raw block.
        Assert.True(HtmlCompiler.IsFullDocument(result));
    }

    [Fact]
    public void Edit_WithStalePath_ReturnsInputUnchanged()
    {
        var result = HtmlStructure.ReplaceElement(Nested, new[] { 5, 9 }, "<ecg></ecg>");
        Assert.Equal(Nested, result);
    }

    // ── Inserting a <p> (Text component) must not lose content ──────────────

    private const string Page =
        "<div class=\"card\"><h1>Title</h1><p class=\"desc\">Body text</p></div>";
    private const string PageDoc =
        "<!DOCTYPE html><html><head><style>.c{}</style></head><body>" + Page + "</body></html>";

    [Fact]
    public void InsertParagraph_AfterNestedNode_FragmentKeepsEverything()
    {
        var card = HtmlStructure.Outline(Page)[0];
        var p = card.Children[1]; // <p class="desc">
        var result = HtmlStructure.InsertAdjacent(Page, p.Path, "<p id=\"x\">Inserted</p>", after: true);
        Assert.Contains("Inserted", result);
        Assert.Contains("<h1>Title</h1>", result);      // sibling kept
        Assert.Contains("class=\"card\"", result);       // ancestor kept
        Assert.Contains("Body text", result);            // target kept
    }

    [Fact]
    public void InsertParagraph_AfterNestedNode_FullDocKeepsEverything()
    {
        var card = HtmlStructure.Outline(PageDoc)[0];
        var p = card.Children[1];
        var result = HtmlStructure.InsertAdjacent(PageDoc, p.Path, "<p id=\"x\">Inserted</p>", after: true);
        Assert.Contains("Inserted", result);
        Assert.Contains("<h1>Title</h1>", result);
        Assert.Contains("<style>.c{}</style>", result);  // head kept
        Assert.True(HtmlCompiler.IsFullDocument(result));
    }

    [Fact]
    public void ReplaceWithParagraph_OnNestedNode_KeepsSiblingsAndHead()
    {
        var card = HtmlStructure.Outline(PageDoc)[0];
        var p = card.Children[1];
        var result = HtmlStructure.ReplaceElement(PageDoc, p.Path, "<p id=\"x\">New body</p>");
        Assert.Contains("New body", result);
        Assert.DoesNotContain("Body text", result);      // targeted node replaced
        Assert.Contains("<h1>Title</h1>", result);       // sibling kept
        Assert.Contains("<style>.c{}</style>", result);  // head kept
    }

    [Fact]
    public void InsertParagraph_AfterRootNode_FragmentKeepsRoot()
    {
        var root = HtmlStructure.Outline(Page)[0]; // the card div
        var result = HtmlStructure.InsertAdjacent(Page, root.Path, "<p id=\"x\">Inserted</p>", after: true);
        Assert.Contains("Inserted", result);
        Assert.Contains("class=\"card\"", result);       // the whole page must remain
        Assert.Contains("<h1>Title</h1>", result);
    }

    [Fact]
    public void AppendChild_AddsAsLastChild_KeepingExisting()
    {
        var card = HtmlStructure.Outline(Page)[0]; // the card container
        var result = HtmlStructure.AppendChild(Page, card.Path, "<p id=\"x\">Added</p>");
        Assert.Contains("Added", result);
        Assert.Contains("<h1>Title</h1>", result);       // existing children kept
        Assert.Contains("Body text", result);
        Assert.True(result.IndexOf("Body text") < result.IndexOf("Added"));  // appended last
        Assert.True(result.IndexOf("Added") < result.IndexOf("</div>"));     // still inside the card
    }

    [Fact]
    public void Outline_SurfacesEcgSegmentNode()
    {
        const string html = "<div><ecgsegment pathology=\"sinus\" lead=\"II\"></ecgsegment></div>";
        var seg = Assert.Single(HtmlStructure.Outline(html)[0].Children);
        Assert.Equal("ecgsegment", seg.Tag);
        Assert.Equal(Kind.Ecg, seg.Kind);
        Assert.Equal("ECG segment", seg.Label);
        Assert.Equal("sinus", seg.Preview);
    }

    [Fact]
    public void RemoveElement_DeletesTargetKeepingSiblingsAndAncestors()
    {
        // Page = <div class="card"><h1>Title</h1><p class="desc">Body text</p></div>
        var p = HtmlStructure.Outline(Page)[0].Children[1]; // <p class="desc">
        var result = HtmlStructure.RemoveElement(Page, p.Path);
        Assert.DoesNotContain("Body text", result);       // target gone
        Assert.Contains("<h1>Title</h1>", result);         // sibling kept
        Assert.Contains("class=\"card\"", result);         // ancestor kept
    }

    [Fact]
    public void RemoveElement_StalePath_ReturnsInputUnchanged()
    {
        Assert.Equal(Page, HtmlStructure.RemoveElement(Page, new[] { 9, 9 }));
    }

    [Fact]
    public void GetOuterHtml_ReturnsTheElementsMarkup()
    {
        var p = HtmlStructure.Outline(Page)[0].Children[1]; // <p class="desc">
        var outer = HtmlStructure.GetOuterHtml(Page, p.Path);
        Assert.NotNull(outer);
        Assert.Contains("Body text", outer!);
        Assert.StartsWith("<p", outer);
        Assert.Null(HtmlStructure.GetOuterHtml(Page, new[] { 9, 9 })); // stale path
    }

    [Fact]
    public void AppendToRoot_IntoEmptyBody_BecomesTheContent()
    {
        Assert.Equal("<p>hi</p>", HtmlStructure.AppendToRoot("", "<p>hi</p>"));
    }

    [Fact]
    public void AppendToRoot_IntoFragment_AddsAtTopLevel()
    {
        var result = HtmlStructure.AppendToRoot("<h3>T</h3>", "<p>added</p>");
        Assert.Contains("<h3>T</h3>", result);
        Assert.Contains("added", result);
        Assert.True(result.IndexOf("T") < result.IndexOf("added"));
    }

    [Fact]
    public void AppendToRoot_IntoFullDocument_InsertsInsideBody()
    {
        const string doc = "<!DOCTYPE html><html><head><style>.x{}</style></head><body><div>a</div></body></html>";
        var result = HtmlStructure.AppendToRoot(doc, "<p>added</p>");
        Assert.Contains("<style>.x{}</style>", result);                    // head untouched
        Assert.Contains("added", result);
        Assert.True(result.IndexOf("added") < result.IndexOf("</body>", System.StringComparison.OrdinalIgnoreCase));
        Assert.True(HtmlCompiler.IsFullDocument(result));
    }
}
