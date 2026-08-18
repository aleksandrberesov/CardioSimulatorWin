using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class HtmlCompilerEcgExtractTests
{
    [Fact]
    public void Extract_ReturnsDistinctIds_InFirstSeenOrder()
    {
        const string html =
            "<p>intro</p>" +
            "<ecg id=\"a\" pathology=\"afib\" leads=\"II\"></ecg>" +
            "<ecgsegment id=\"b\" pathology=\"stemi\" lead=\"V2\" start=\"0\" duration=\"2\"></ecgsegment>" +
            "<ecg id=\"c\" pathology=\"afib\"></ecg>"; // duplicate afib

        var ids = HtmlCompiler.ExtractEcgPathologyIds(html);

        Assert.Equal(new[] { "afib", "stemi" }, ids);
    }

    [Fact]
    public void Extract_FindsNestedEmbeds_InsideCardsAndFigures()
    {
        const string html =
            "<div class=\"lecture-card\"><figure><ecg id=\"x\" pathology=\"sinus\"></ecg></figure></div>";

        var ids = HtmlCompiler.ExtractEcgPathologyIds(html);

        Assert.Equal(new[] { "sinus" }, ids);
    }

    [Fact]
    public void Extract_SkipsBlankOrMissingPathology()
    {
        const string html =
            "<ecg id=\"a\" pathology=\"\"></ecg><ecg id=\"b\"></ecg><ecg id=\"c\" pathology=\"vt\"></ecg>";

        var ids = HtmlCompiler.ExtractEcgPathologyIds(html);

        Assert.Equal(new[] { "vt" }, ids);
    }

    [Fact]
    public void Extract_EmptyOrNullHtml_ReturnsEmpty()
    {
        Assert.Empty(HtmlCompiler.ExtractEcgPathologyIds(""));
        Assert.Empty(HtmlCompiler.ExtractEcgPathologyIds("   "));
        Assert.Empty(HtmlCompiler.ExtractEcgPathologyIds("<p>no ecg here</p>"));
    }
}
