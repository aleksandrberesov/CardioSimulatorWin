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
    public void ResolvedNameRu_ReturnsNull_WhenUntaggedAndNameRuNullAndTitleEnUnknown()
    {
        var entry = new PathologyEntry("raw_1", "Raw Unknown Rhythm XYZ", null, 12, "raw_1.dat");
        Assert.Null(entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_TranslatesEnglishTitle_WhenUntaggedAndNameRuNull()
    {
        var entry = new PathologyEntry("sb_1", "Sinus Bradycardia", null, 12, "sb_1.dat");
        Assert.Equal("Синусовая брадикардия", entry.ResolvedNameRu);
    }

    [Fact]
    public void ResolveTextRu_TranslatesCompoundEnglishFindings()
    {
        var text = "1 degree atrioventricular block + 2 degree atrioventricular block(Type one) + left ventricle hypertrophy + Sinus Bradycardia + T wave Change";
        var translated = PathologyTranslationHelpers.ResolveTextRu(text);
        Assert.NotNull(translated);
        Assert.Contains("АВ-блокада 1 степени", translated);
        Assert.Contains("АВ-блокада 2 ст. (Мобитц I)", translated);
        Assert.Contains("Гипертрофия левого желудочка", translated);
        Assert.Contains("Синусовая брадикардия", translated);
        Assert.Contains("Изменение зубца T", translated);
    }

    [Fact]
    public void ResolveTextRu_TranslatesUserScreenshotSpecificFindings()
    {
        var text1 = "Sinus Tachycardia + lower voltage QRS in all lead";
        var translated1 = PathologyTranslationHelpers.ResolveTextRu(text1);
        Assert.Equal("Синусовая тахикардия + Низкий вольтаж QRS (все отвед.)", translated1);

        var text2 = "1 degree atrioventricular block + Artificial pacing rhythm";
        var translated2 = PathologyTranslationHelpers.ResolveTextRu(text2);
        Assert.Equal("АВ-блокада 1 степени + Ритм ЭКС (искусственный)", translated2);
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
        var file = new PathologyFile(
            Id: "comp_1",
            TitleEn: "Bradycardia with LVH",
            NameRu: null,
            Leads: new Dictionary<Lead, LeadStream>())
        {
            Acronyms = new[] { "SB", "LVH" }
        };
        Assert.Equal("Синусовая брадикардия, Гипертрофия левого желудочка", file.ResolvedNameRu);
    }

    [Fact]
    public void ResolvedNameRu_TranslatesCombinedRhythmsWithAbbreviations()
    {
        var entry1 = new PathologyEntry("pac_1", "Sinus rhythm + PACs", null, 12, "pac_1.dat");
        Assert.Equal("Синусовый ритм + Предсердная экстрасистолия", entry1.ResolvedNameRu);

        var entry2 = new PathologyEntry("pvc_1", "Sinus rhythm with PVCs", null, 12, "pvc_1.dat");
        Assert.Equal("Синусовый ритм + Желудочковая экстрасистолия", entry2.ResolvedNameRu);

        var entry3 = new PathologyEntry("mob_1", "Sinus rhythm + Mobitz I", null, 12, "mob_1.dat");
        Assert.Equal("Синусовый ритм + АВ-блокада 2 ст. (Мобитц I)", entry3.ResolvedNameRu);

        var entry4 = new PathologyEntry("avb_1", "Sinus rhythm + 2 degree AV block", null, 12, "avb_1.dat");
        Assert.Equal("Синусовый ритм + АВ-блокада 2 степени", entry4.ResolvedNameRu);
    }
}
