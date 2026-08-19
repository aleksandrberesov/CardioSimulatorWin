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
