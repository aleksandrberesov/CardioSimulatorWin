using System.Collections.Generic;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class HtmlCompilerTests
{
    [Fact]
    public void Parse_RecognizesBlockTypes()
    {
        const string html =
            "<h2 id=\"a\">Title</h2>\n" +
            "<p id=\"b\">Hello world</p>\n" +
            "<ecg id=\"c\" pathology=\"abc\" lead=\"II\" caption=\"cap\"></ecg>";

        var blocks = HtmlCompiler.Parse(html);

        Assert.Equal(3, blocks.Count);

        var header = Assert.IsType<HtmlBlock.Header>(blocks[0]);
        Assert.Equal(2, header.Level);
        Assert.Equal("Title", header.Text);
        Assert.Equal("a", header.Id);

        var paragraph = Assert.IsType<HtmlBlock.Paragraph>(blocks[1]);
        Assert.Equal("b", paragraph.Id);
        Assert.Contains("Hello", paragraph.Html);

        var ecg = Assert.IsType<HtmlBlock.Ecg>(blocks[2]);
        Assert.Equal("abc", ecg.Pathology);
        Assert.Equal(new[] { Lead.II }, ecg.Leads);
        Assert.Equal(SeriesScheme.OneColumn, ecg.Scheme);
        Assert.Equal("cap", ecg.Caption);
    }

    [Fact]
    public void Parse_MultipleLeadsAndScheme_AreCanonicalSorted()
    {
        var blocks = HtmlCompiler.Parse(
            "<ecg pathology=\"abc\" leads=\"V1, II ,V5\" scheme=\"grid\"></ecg>");
        var ecg = Assert.IsType<HtmlBlock.Ecg>(Assert.Single(blocks));
        Assert.Equal(new[] { Lead.II, Lead.V1, Lead.V5 }, ecg.Leads);
        Assert.Equal(SeriesScheme.Grid, ecg.Scheme);
    }

    [Fact]
    public void Parse_NoLeads_YieldsEmptyListMeaningAllLeads()
    {
        var blocks = HtmlCompiler.Parse("<ecg pathology=\"abc\"></ecg>");
        var ecg = Assert.IsType<HtmlBlock.Ecg>(Assert.Single(blocks));
        Assert.Empty(ecg.Leads);
        Assert.Equal(SeriesScheme.OneColumn, ecg.Scheme);
    }

    [Fact]
    public void Parse_DisplayMathParagraph_BecomesKaTeX()
    {
        var blocks = HtmlCompiler.Parse("<p>$$ E = mc^2 $$</p>");
        var katex = Assert.IsType<HtmlBlock.KaTeX>(Assert.Single(blocks));
        Assert.True(katex.DisplayMode);
        Assert.Equal("E = mc^2", katex.Expression);
    }

    [Fact]
    public void Parse_Table_ReadsRowsAndCells()
    {
        var blocks = HtmlCompiler.Parse(
            "<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>");
        var table = Assert.IsType<HtmlBlock.Table>(Assert.Single(blocks));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "a", "b" }, table.Rows[0]);
        Assert.Equal(new[] { "c", "d" }, table.Rows[1]);
    }

    [Fact]
    public void CompileThenParse_RoundTripsBlocksAndIds()
    {
        var blocks = new List<HtmlBlock>
        {
            new HtmlBlock.Header(1, "Heading") { Id = "h1" },
            new HtmlBlock.Paragraph("body text") { Id = "p1" },
            new HtmlBlock.KaTeX("a^2 + b^2", DisplayMode: true) { Id = "k1" },
            new HtmlBlock.Ecg("pathId", new[] { Lead.V1, Lead.V2 }, SeriesScheme.Grid, "caption") { Id = "e1" },
            new HtmlBlock.Table(new List<IReadOnlyList<string>> { new List<string> { "x", "y" } }) { Id = "t1" },
        };

        var round = HtmlCompiler.Parse(HtmlCompiler.Compile(blocks));

        Assert.Equal(5, round.Count);
        Assert.Equal("h1", Assert.IsType<HtmlBlock.Header>(round[0]).Id);
        Assert.IsType<HtmlBlock.Paragraph>(round[1]);
        var k = Assert.IsType<HtmlBlock.KaTeX>(round[2]);
        Assert.Equal("a^2 + b^2", k.Expression);
        var e = Assert.IsType<HtmlBlock.Ecg>(round[3]);
        Assert.Equal("pathId", e.Pathology);
        Assert.Equal(new[] { Lead.V1, Lead.V2 }, e.Leads);
        Assert.Equal(SeriesScheme.Grid, e.Scheme);
        Assert.Equal("e1", e.Id);
        Assert.IsType<HtmlBlock.Table>(round[4]);
    }

    [Fact]
    public void Parse_EmptyHtml_YieldsNoBlocks()
    {
        Assert.Empty(HtmlCompiler.Parse(""));
        Assert.Empty(HtmlCompiler.Parse("   "));
    }
}
