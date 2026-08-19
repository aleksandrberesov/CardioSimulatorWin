using System;
using System.Collections.Generic;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class PathologyTranslationTests
{
    [Fact]
    public void ResolvedNameRu_ReturnsExplicitNameRu_WhenPresent()
    {
        var entry = new PathologyEntry("sb_1", "Sinus Bradycardia", "Моя Брадикардия", 12, "sb_1.dat", Acronyms: new[] { "SB" });
        Assert.Equal("Моя Брадикардия", entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_TranslatesSingleAcronymFromTaxonomy_WhenNameRuNull()
    {
        var entry = new PathologyEntry("sb_1", "Sinus Bradycardia", null, 12, "sb_1.dat", Acronyms: new[] { "SB" });
        Assert.Equal("Синусовая брадикардия", entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_TranslatesCompositeAcronymsFromTaxonomy_WhenNameRuNull()
    {
        var entry = new PathologyEntry("comp_1", "Bradycardia with LVH", null, 12, "comp_1.dat", Acronyms: new[] { "SB", "LVH" });
        Assert.Equal("Синусовая брадикардия, Гипертрофия левого желудочка", entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_ReturnsNull_WhenUntaggedAndNameRuNull()
    {
        var entry = new PathologyEntry("raw_1", "Raw Rhythm", null, 12, "raw_1.dat");
        Assert.Null(entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_IgnoresUnknownAcronyms()
    {
        var entry = new PathologyEntry("mix_1", "Mixed Rhythm", null, 12, "mix_1.dat", Acronyms: new[] { "NON_EXISTENT_CODE", "LVH" });
        Assert.Equal("Гипертрофия левого желудочка", entry.ResolvedNameRu);
    }

    [Fact]
    public void PathologyFile_ResolvedNameRu_BehavesIdentically()
    {
        var file = new PathologyFile("comp_1", "Bradycardia with LVH", null, new Dictionary<Lead, LeadStream>())
        {
            Acronyms = new[] { "SB", "LVH" }
        };
        Assert.Equal("Синусовая брадикардия, Гипертрофия левого желудочка", file.ResolvedNameRu);
    }
}
