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

    [Fact]
    public void Parse_NestedMarkup_BecomesRawBlock()
    {
        const string html = "<div class=\"scheme\"><svg viewBox=\"0 0 10 10\"><path d=\"M1,2 L3,4\"/></svg></div>";
        var raw = Assert.IsType<HtmlBlock.Raw>(Assert.Single(HtmlCompiler.Parse(html)));
        Assert.Contains("<svg", raw.Html);
        Assert.Contains("<path", raw.Html);
    }

    [Fact]
    public void CompileParse_NestedMarkup_RoundTripsWithoutParagraphWrapper()
    {
        // Regression: a nested <div> must not be shoehorned into a <p> (which corrupts block-level
        // nesting), and its inner structure must survive the round-trip.
        const string html = "<div class=\"scheme\"><svg viewBox=\"0 0 10 10\"><path d=\"M1,2 L3,4\"/></svg></div>";
        var compiled = HtmlCompiler.Compile(HtmlCompiler.Parse(html));
        Assert.DoesNotContain("<p ", compiled);
        Assert.DoesNotContain("<p>", compiled);
        Assert.Contains("<svg", compiled);
        Assert.Contains("<path", compiled);
        // Re-parsing the compiled output yields a single Raw block again (stable).
        Assert.IsType<HtmlBlock.Raw>(Assert.Single(HtmlCompiler.Parse(compiled)));
    }

    [Fact]
    public void Compile_RawWithoutId_StampsBlockIdOnRootForScrollSync()
    {
        var block = new HtmlBlock.Raw("<div class=\"scheme\"><span>hi</span></div>") { Id = "blk1" };
        var compiled = HtmlCompiler.Compile(new HtmlBlock[] { block });
        Assert.Contains("id=\"blk1\"", compiled);
        Assert.Contains("class=\"scheme\"", compiled);
    }

    [Fact]
    public void Parse_FullDocument_BecomesSingleVerbatimRawBlock()
    {
        const string html = "<!DOCTYPE html><html><head><style>.card{color:red}</style></head>" +
                            "<body><div class=\"card\">Hi</div></body></html>";
        var raw = Assert.IsType<HtmlBlock.Raw>(Assert.Single(HtmlCompiler.Parse(html)));
        Assert.Equal(html, raw.Html); // verbatim — head/style untouched

        var compiled = HtmlCompiler.Compile(HtmlCompiler.Parse(html));
        Assert.Contains("<style>.card{color:red}</style>", compiled);
        Assert.Contains("<div class=\"card\">", compiled);
    }

    [Fact]
    public void EmbedDocument_ScopesStylesAndWrapsBody_AsAFragment()
    {
        const string doc =
            "<!DOCTYPE html><html><head><style>" +
            "body{display:flex;min-height:100vh;color:red}" +
            ".card{padding:2rem}" +
            "h1{font-size:2rem}" +
            "@media (max-width:600px){.card{padding:1rem}}" +
            "@keyframes spin{from{opacity:0}to{opacity:1}}" +
            "</style></head><body><div class=\"card\"><h1>Hi</h1><script>alert(1)</script></div></body></html>";

        var embed = HtmlCompiler.EmbedDocument(doc);

        // Now a body fragment (lays out inside the app template next to app components).
        Assert.False(HtmlCompiler.IsFullDocument(embed));
        Assert.StartsWith("<div class=\"lecture-embed\">", embed);
        Assert.DoesNotContain("<html", embed);
        Assert.DoesNotContain("<head", embed);

        // Page-level selectors scoped to the wrapper; class/element selectors prefixed.
        Assert.Contains(".lecture-embed{", embed);         // body → .lecture-embed
        Assert.Contains(".lecture-embed .card{", embed);   // .card scoped
        Assert.Contains(".lecture-embed h1{", embed);      // element scoped
        // @media recursed; @keyframes left intact.
        Assert.Contains("@media", embed);
        Assert.Contains(".lecture-embed .card{padding:1rem}", embed);
        Assert.Contains("@keyframes spin{", embed);

        // Viewport height dropped; scripts dropped; body content kept.
        Assert.DoesNotContain("100vh", embed);
        Assert.DoesNotContain("<script", embed);
        Assert.Contains("<div class=\"card\">", embed);
        Assert.Contains("<h1>Hi</h1>", embed);
    }

    [Fact]
    public void EmbedDocument_RoundTripsAsSingleRawBlock()
    {
        const string doc = "<!DOCTYPE html><html><head><style>.card{color:red}</style></head>" +
                          "<body><div class=\"card\">Hi</div></body></html>";
        var embed = HtmlCompiler.EmbedDocument(doc);

        var raw = Assert.IsType<HtmlBlock.Raw>(Assert.Single(HtmlCompiler.Parse(embed)));
        var compiled = HtmlCompiler.Compile(new HtmlBlock[] { raw });
        Assert.Contains("class=\"lecture-embed\"", compiled);
        Assert.Contains(".lecture-embed .card{color:red}", compiled);
        Assert.DoesNotContain("<p>", compiled);
    }

    [Fact]
    public void StructuralBlocks_RoundTripThroughCompileParse()
    {
        var blocks = new HtmlBlock[]
        {
            new HtmlBlock.List(new[] { "a", "b" }, Numbered: true) { Id = "l1" },
            new HtmlBlock.Quote("<b>wise</b> words") { Id = "q1" },
            new HtmlBlock.Note("warning", "careful") { Id = "n1" },
            new HtmlBlock.Card("Title", "body") { Id = "c1" },
            new HtmlBlock.Section("Sec", "sbody") { Id = "s1" },
            new HtmlBlock.Figure("<span>x</span>", "cap") { Id = "f1" },
            new HtmlBlock.Divider() { Id = "d1" },
        };

        var round = HtmlCompiler.Parse(HtmlCompiler.Compile(blocks));
        Assert.Equal(7, round.Count);

        var list = Assert.IsType<HtmlBlock.List>(round[0]);
        Assert.Equal(new[] { "a", "b" }, list.Items);
        Assert.True(list.Numbered);
        Assert.Equal("l1", list.Id);

        Assert.Contains("wise", Assert.IsType<HtmlBlock.Quote>(round[1]).Html);

        var note = Assert.IsType<HtmlBlock.Note>(round[2]);
        Assert.Equal("warning", note.Variant);
        Assert.Contains("careful", note.Html);

        var card = Assert.IsType<HtmlBlock.Card>(round[3]);
        Assert.Equal("Title", card.Title);
        Assert.Contains("body", card.Html);

        Assert.Equal("Sec", Assert.IsType<HtmlBlock.Section>(round[4]).Title);

        var figure = Assert.IsType<HtmlBlock.Figure>(round[5]);
        Assert.Equal("cap", figure.Caption);
        Assert.Contains("x", figure.Html);

        Assert.IsType<HtmlBlock.Divider>(round[6]);
        Assert.Equal("d1", round[6].Id);
    }

    [Fact]
    public void ContainerBlock_RoundTripsAndHoldsNestedContent()
    {
        var block = new HtmlBlock.Container("<h3>Hi</h3><ul class=\"lecture-list\"><li>a</li></ul>") { Id = "ct1" };
        var round = HtmlCompiler.Parse(HtmlCompiler.Compile(new HtmlBlock[] { block }));
        var container = Assert.IsType<HtmlBlock.Container>(Assert.Single(round));
        Assert.Equal("ct1", container.Id);
        Assert.Contains("<h3>Hi</h3>", container.Html);
        Assert.Contains("lecture-list", container.Html);
    }

    [Fact]
    public void Parse_ForeignContainer_StaysRaw()
    {
        // A page's own div classes are unrecognized → kept verbatim as Raw (not mis-typed as a Card).
        var raw = Assert.IsType<HtmlBlock.Raw>(Assert.Single(
            HtmlCompiler.Parse("<div class=\"section-block\" style=\"padding:2rem\"><p>x</p></div>")));
        Assert.Contains("section-block", raw.Html);
        Assert.Contains("padding:2rem", raw.Html);
    }

    [Fact]
    public void EcgSegment_RoundTripsThroughCompileParse()
    {
        var seg = new HtmlBlock.EcgSegment("path1", Lead.V2, 1.5, 2.0, "cap") { Id = "seg1" };
        var tag = HtmlCompiler.BuildEcgSegmentTag(seg);
        Assert.Contains("pathology=\"path1\"", tag);
        Assert.Contains("lead=\"V2\"", tag);
        Assert.Contains("start=\"1.5\"", tag);
        Assert.Contains("duration=\"2\"", tag);

        var parsed = Assert.IsType<HtmlBlock.EcgSegment>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal("path1", parsed.Pathology);
        Assert.Equal(Lead.V2, parsed.Lead);
        Assert.Equal(1.5, parsed.StartSec);
        Assert.Equal(2.0, parsed.DurationSec);
        Assert.Equal("cap", parsed.Caption);
        Assert.Equal("seg1", parsed.Id);
        // No filter selected → no attribute emitted, and it parses back as None.
        Assert.DoesNotContain("filter=", tag);
        Assert.Equal(EcgFilterType.None, parsed.Filter);
        // No explicit size → no width/height attributes, and both parse back as null (intrinsic).
        Assert.DoesNotContain("width=", tag);
        Assert.DoesNotContain("height=", tag);
        Assert.Null(parsed.WidthPx);
        Assert.Null(parsed.HeightPx);
    }

    [Fact]
    public void EcgSegment_WithSize_RoundTripsThroughCompileParse()
    {
        var seg = new HtmlBlock.EcgSegment("p1", Lead.II, 0, 2, "cap") { WidthPx = 320, HeightPx = 160 };
        var tag = HtmlCompiler.BuildEcgSegmentTag(seg);
        Assert.Contains("width=\"320\"", tag);
        Assert.Contains("height=\"160\"", tag);

        var parsed = Assert.IsType<HtmlBlock.EcgSegment>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal(320, parsed.WidthPx);
        Assert.Equal(160, parsed.HeightPx);
    }

    [Fact]
    public void EcgSegment_WithOnlyWidth_OmitsHeightAttribute()
    {
        var seg = new HtmlBlock.EcgSegment("p1", Lead.II, 0, 2, "cap") { WidthPx = 400 };
        var tag = HtmlCompiler.BuildEcgSegmentTag(seg);
        Assert.Contains("width=\"400\"", tag);
        Assert.DoesNotContain("height=", tag);

        var parsed = Assert.IsType<HtmlBlock.EcgSegment>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal(400, parsed.WidthPx);
        Assert.Null(parsed.HeightPx);
    }

    [Fact]
    public void EcgSegment_WithFilter_RoundTripsThroughCompileParse()
    {
        var seg = new HtmlBlock.EcgSegment("p1", Lead.II, 0, 2, "cap") { Filter = EcgFilterType.Bandpass };
        var tag = HtmlCompiler.BuildEcgSegmentTag(seg);
        Assert.Contains("filter=\"bandpass\"", tag);

        var parsed = Assert.IsType<HtmlBlock.EcgSegment>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal(EcgFilterType.Bandpass, parsed.Filter);
    }

    [Fact]
    public void EcgSegment_WithTips_RoundTripsThroughCompileParse()
    {
        var seg = new HtmlBlock.EcgSegment("p1", Lead.II, 0, 2, "cap")
        {
            Tips = new System.Collections.Generic.List<TipOverlay>
            {
                new(TipOverlayKind.VerticalLines, new[] { new TipPoint(100, 0) }),
                new(TipOverlayKind.Label, new[] { new TipPoint(50, 30) }, Text: "note"),
            },
        };
        var tag = HtmlCompiler.BuildEcgSegmentTag(seg);
        Assert.Contains("tips=\"", tag);

        var parsed = Assert.IsType<HtmlBlock.EcgSegment>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal(2, parsed.Tips.Count);
        Assert.Equal(TipOverlayKind.VerticalLines, parsed.Tips[0].Kind);
        Assert.Equal("note", parsed.Tips[1].Text);
    }

    [Fact]
    public void BuildEcgTag_RoundTripsThroughParse()
    {
        var ecg = new HtmlBlock.Ecg("pathId", new[] { Lead.V1, Lead.V2 }, SeriesScheme.Grid, "cap") { Id = "e1" };
        var tag = HtmlCompiler.BuildEcgTag(ecg);
        Assert.Contains("pathology=\"pathId\"", tag);
        Assert.Contains("leads=\"V1,V2\"", tag);
        Assert.Contains("scheme=\"grid\"", tag);

        var parsed = Assert.IsType<HtmlBlock.Ecg>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal("pathId", parsed.Pathology);
        Assert.Equal(new[] { Lead.V1, Lead.V2 }, parsed.Leads);
        Assert.Equal(SeriesScheme.Grid, parsed.Scheme);
        Assert.Equal("cap", parsed.Caption);
        // No filter selected → no attribute emitted, and it parses back as None.
        Assert.DoesNotContain("filter=", tag);
        Assert.Equal(EcgFilterType.None, parsed.Filter);
    }

    [Fact]
    public void BuildEcgTag_WithFilter_RoundTripsThroughParse()
    {
        var ecg = new HtmlBlock.Ecg("p", System.Array.Empty<Lead>(), SeriesScheme.OneColumn, "cap")
        {
            Filter = EcgFilterType.Highpass,
        };
        var tag = HtmlCompiler.BuildEcgTag(ecg);
        Assert.Contains("filter=\"highpass\"", tag);

        var parsed = Assert.IsType<HtmlBlock.Ecg>(Assert.Single(HtmlCompiler.Parse(tag)));
        Assert.Equal(EcgFilterType.Highpass, parsed.Filter);
    }
}
