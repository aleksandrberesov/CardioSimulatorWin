using System.Linq;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TaxonomyTests
{
    [Fact]
    public void Shared_LoadsEmbeddedTable()
    {
        var tax = Taxonomy.Shared;
        Assert.True(tax.Count >= 100, $"expected the full taxonomy, got {tax.Count}");
        // A representative spread across sections resolves.
        Assert.NotNull(tax.Find("AFIB"));
        Assert.NotNull(tax.Find("2AVB1"));
        Assert.NotNull(tax.Find("MI_ANT"));
        Assert.NotNull(tax.Find("APACE"));
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndTrims()
    {
        var tax = Taxonomy.Shared;
        var a = tax.Find("afib");
        var b = tax.Find("  AFIB ");
        Assert.NotNull(a);
        Assert.Equal("AFIB", a!.Acronym);
        Assert.Equal(a, b);
        Assert.True(tax.Contains("Afib"));
        Assert.Null(tax.Find("NOT_A_CODE"));
        Assert.Null(tax.Find(""));
    }

    [Fact]
    public void KnownEntry_HasExpectedMapping()
    {
        var e = Taxonomy.Shared.Find("AFIB")!;
        Assert.Equal("arrhythmia", e.Group);
        Assert.Equal(3, e.Section);
        Assert.Equal("3.3.4", e.Subsection);
        Assert.Equal("3.3", e.SubtopicKey);
    }

    [Fact]
    public void MultiNode_Acronym_KeepsPrimaryAndAlternates()
    {
        var wpw = Taxonomy.Shared.Find("WPW")!;
        Assert.Equal("4.11.1", wpw.Subsection);
        Assert.Equal(4, wpw.Section);
        Assert.Contains("8.1", wpw.AltSubsections);
    }

    [Theory]
    [InlineData("4.6.2", "4.6")]
    [InlineData("6.3", "6.3")]
    [InlineData("3.2", "3.2")]
    [InlineData("10.2", "10.2")]
    [InlineData("5", "5")]
    public void SubtopicKeyOf_TrimsToTwoLevels(string subsection, string expected) =>
        Assert.Equal(expected, Taxonomy.SubtopicKeyOf(subsection));

    [Fact]
    public void Parse_SkipsCommentsAndHeader_AndIsTolerant()
    {
        var tsv = string.Join("\n",
            "# a comment",
            "acronym\tname_ru\tgroup\tsection\tsubsection\tsubsection_title\talt_subsections",
            "sb\tСинусовая брадикардия\tsinus\t3\t3.1.2\tСинусовая брадикардия\t",
            "bad_row_missing_columns",
            "wpw\tСиндром WPW\tsyndromes\t4\t4.11.1\tWPW\t8.1");
        var tax = Taxonomy.Parse(tsv);
        Assert.Equal(2, tax.Count);
        Assert.Equal("SB", tax.Find("sb")!.Acronym); // normalized upper-case
        Assert.Contains("8.1", tax.Find("WPW")!.AltSubsections);
    }

    [Fact]
    public void ForSubtopic_GroupsSiblingNodes()
    {
        // Everything under AV block I/II/III rolls into subtopic 4.6.
        var codes = Taxonomy.Shared.ForSubtopic("4.6").Select(e => e.Acronym).ToHashSet();
        Assert.Contains("1AVB", codes);
        Assert.Contains("2AVB1", codes);
        Assert.Contains("3AVB", codes);
    }

}
