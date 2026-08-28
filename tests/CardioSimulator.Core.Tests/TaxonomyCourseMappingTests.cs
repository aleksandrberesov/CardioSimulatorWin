using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TaxonomyCourseMappingTests
{
    [Fact]
    public void ForSubsectionOrTopic_ResolvesAcronymsBySubsectionKey()
    {
        var entries = Taxonomy.Shared.ForSubsectionOrTopic("4.6.2").ToList();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Acronym == "2AVB1");
    }

    [Fact]
    public void ForSubsectionOrTopic_ResolvesAcronymsBySubtopicKey()
    {
        var entries = Taxonomy.Shared.ForSubsectionOrTopic("4.6").ToList();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Acronym == "1AVB");
        Assert.Contains(entries, e => e.Acronym == "3AVB");
    }

    [Fact]
    public void ForSubsectionOrTopic_ResolvesAcronymsBySectionNumber()
    {
        var entries = Taxonomy.Shared.ForSubsectionOrTopic("4").ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(4, e.Section));
    }

    // Regression for the test-generator «Определи ЭКГ» + theme bug (customer feedback 28-08-2026): picking
    // a course theme by name (e.g. «Раздел 4. …» or a numbered sub-topic «4.6. …») must resolve to that
    // section/subsection's acronyms, so the on-the-fly ECG synthesizer can build questions for a theme-only
    // selection. This exercises the exact chain GenSynthesisAcronyms uses: title → CourseNumbering.NumberPrefix
    // → Taxonomy.ForSubsectionOrTopic. Before the fix, synthesis ran off the acronym picker only, so a
    // theme-only pick with no matching bank questions produced an empty test.
    [Fact]
    public void SectionThemeTitle_ResolvesToSectionAcronyms()
    {
        var key = CourseNumbering.NumberPrefix("Раздел 4. Нарушения ритма и проводимости");
        Assert.Equal("4", key);

        var acronyms = Taxonomy.Shared.ForSubsectionOrTopic(key).Select(e => e.Acronym).ToList();
        Assert.NotEmpty(acronyms);
        Assert.Equal(
            Taxonomy.Shared.ForSection(4).Select(e => e.Acronym).OrderBy(a => a),
            acronyms.OrderBy(a => a));
    }

    [Fact]
    public void SubtopicThemeTitle_ResolvesToSubtopicAcronyms()
    {
        var key = CourseNumbering.NumberPrefix("4.6. Атриовентрикулярные блокады");
        Assert.Equal("4.6", key);

        var acronyms = Taxonomy.Shared.ForSubsectionOrTopic(key).Select(e => e.Acronym).ToList();
        Assert.Contains("1AVB", acronyms);
        Assert.Contains("3AVB", acronyms);
    }

    [Fact]
    public void ResolvePathologyIdsForAcronyms_MatchesPathologiesWithTaxonomyAcronyms()
    {
        var pathologies = new List<PathologyEntry>
        {
            new PathologyEntry("path_avb", "AV Block", "АВ Блокада", 12, "p1.dat", Acronyms: new[] { "2AVB1" }),
            new PathologyEntry("path_sb", "Sinus Brady", "Синусовая брадикардия", 12, "p2.dat", Acronyms: new[] { "SB" }),
            new PathologyEntry("path_untagged", "Raw", null, 12, "p3.dat")
        };

        var matchedIds = Taxonomy.ResolvePathologyIdsForAcronyms(new[] { "2AVB1", "3AVB" }, pathologies);
        Assert.Single(matchedIds);
        Assert.Equal("path_avb", matchedIds[0]);
    }
}
