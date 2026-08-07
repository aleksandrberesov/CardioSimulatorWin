using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class HtmlComponentsTests
{
    [Fact]
    public void List_Bulleted_And_Numbered()
    {
        var ul = HtmlComponents.List(new[] { "a", "b" }, numbered: false);
        Assert.StartsWith("<ul class=\"lecture-list\">", ul);
        Assert.Contains("<li>a</li><li>b</li>", ul);
        Assert.EndsWith("</ul>", ul);

        var ol = HtmlComponents.List(new[] { "x" }, numbered: true);
        Assert.StartsWith("<ol class=\"lecture-list\">", ol);
        Assert.EndsWith("</ol>", ol);
    }

    [Fact]
    public void Card_WithAndWithoutTitle()
    {
        var withTitle = HtmlComponents.Card("T", "Body");
        Assert.Contains("class=\"lecture-card\"", withTitle);
        Assert.Contains("<div class=\"lecture-card-title\">T</div>", withTitle);
        Assert.Contains("<div class=\"lecture-card-body\">Body</div>", withTitle);

        var noTitle = HtmlComponents.Card(null, "Body");
        Assert.DoesNotContain("lecture-card-title", noTitle);
        Assert.Contains("Body", noTitle);
    }

    [Fact]
    public void Section_HasHeadingAndBody()
    {
        var s = HtmlComponents.Section("Title", "Body");
        Assert.StartsWith("<section class=\"lecture-section\">", s);
        Assert.Contains("<h3 class=\"lecture-section-title\">Title</h3>", s);
        Assert.Contains("<div class=\"lecture-section-body\">Body</div>", s);
    }

    [Fact]
    public void Note_AppliesVariantClass()
    {
        Assert.Contains("lecture-note lecture-note-warning", HtmlComponents.Note("warning", "Careful"));
        Assert.Contains("lecture-note lecture-note-info", HtmlComponents.Note("", "Default"));   // blank → info
        Assert.Contains("Careful", HtmlComponents.Note("warning", "Careful"));
    }

    [Fact]
    public void Quote_WithOptionalCite()
    {
        Assert.Contains("<cite>Author</cite>", HtmlComponents.Quote("Wise words", "Author"));
        Assert.DoesNotContain("<cite>", HtmlComponents.Quote("Wise words", null));
    }

    [Fact]
    public void Figure_WithOptionalCaption()
    {
        var f = HtmlComponents.Figure("content", "A caption");
        Assert.Contains("class=\"lecture-figure\"", f);
        Assert.Contains("<figcaption>A caption</figcaption>", f);
        Assert.DoesNotContain("<figcaption>", HtmlComponents.Figure("content", null));
    }

    [Fact]
    public void Divider_IsHr() => Assert.Equal("<hr class=\"lecture-divider\">", HtmlComponents.Divider());

    [Fact]
    public void Components_AreFragments_ThatSurviveInsertion()
    {
        // A structural component inserted into a page keeps everything else intact.
        const string page = "<div class=\"card\"><h1>T</h1></div>";
        var node = HtmlStructure.Outline(page)[0];
        var result = HtmlStructure.AppendChild(page, node.Path, HtmlComponents.Note("tip", "Remember"));
        Assert.Contains("<h1>T</h1>", result);
        Assert.Contains("lecture-note-tip", result);
        Assert.Contains("Remember", result);
    }
}
