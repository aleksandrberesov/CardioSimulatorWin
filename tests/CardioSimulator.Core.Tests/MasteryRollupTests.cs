using System;
using System.Collections.Generic;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class MasteryRollupTests
{
    private static Taxonomy SmallTaxonomy() => Taxonomy.Parse(string.Join("\n",
        "acronym\tname_ru\tgroup\tsection\tsubsection\tsubsection_title\talt_subsections",
        "SB\tСинусовая брадикардия\tsinus\t3\t3.1.2\tСинусовая брадикардия\t",
        "AFIB\tФибрилляция предсердий\tarrhythmia\t3\t3.3.4\tФибрилляция предсердий\t",
        "MI_ANT\tПередний ИМ\tinfarction\t6\t6.3\tТопическая диагностика ИМ\t",
        "MI_ANT_STEMI\tПередний ИМ с ST\tinfarction\t6\t6.3\tТопическая диагностика ИМ\t"));

    private static ExamResult ResultWith(params ExamQuestionResult[] qs) =>
        new(new ExamStudentInfo("Иванов", "К-1"), "t1", "Test 1",
            DateTimeOffset.UnixEpoch, qs, 0, qs.Length, false);

    private static ExamQuestionResult Q(bool correct, params string[] acronyms) =>
        new("q" + Guid.NewGuid().ToString("N"), correct ? "a" : "b", "a", correct, acronyms);

    [Fact]
    public void Compute_AggregatesBySubtopicSectionAndGroup()
    {
        var tax = SmallTaxonomy();
        var results = new[]
        {
            ResultWith(
                Q(correct: true,  "SB"),
                Q(correct: false, "SB"),
                Q(correct: true,  "AFIB")),
        };

        var report = MasteryRollup.Compute(results, tax);

        Assert.True(report.HasData);
        Assert.Equal(3, report.TotalAnswered);
        Assert.Equal(2, report.TotalCorrect);

        // Subtopic 3.1 = SB: 1/2 = 50%. Subtopic 3.3 = AFIB: 1/1 = 100%.
        Assert.Equal(50, report.Subtopic("3.1").Progress);
        Assert.Equal(100, report.Subtopic("3.3").Progress);

        // Section 3 = all three: 2/3 = 67%.
        Assert.Equal(3, report.Section(3).Answered);
        Assert.Equal(67, report.Section(3).Progress);

        // Group tallies.
        Assert.Equal(new MasteryStat(2, 1), report.ByGroup["sinus"]);
        Assert.Equal(new MasteryStat(1, 1), report.ByGroup["arrhythmia"]);
    }

    [Fact]
    public void Compute_TwoAcronymsSameSubtopic_CountOnce()
    {
        var tax = SmallTaxonomy();
        // Both MI_ANT and MI_ANT_STEMI roll into subtopic 6.3 — one answer must count once there.
        var results = new[] { ResultWith(Q(correct: true, "MI_ANT", "MI_ANT_STEMI")) };

        var report = MasteryRollup.Compute(results, tax);

        Assert.Equal(1, report.Subtopic("6.3").Answered);
        Assert.Equal(1, report.Subtopic("6.3").Correct);
        Assert.Equal(1, report.Section(6).Answered);
        Assert.Equal(1, report.TotalAnswered);
    }

    [Fact]
    public void Compute_IgnoresUntaggedAndUnknownAcronyms()
    {
        var tax = SmallTaxonomy();
        var results = new[]
        {
            ResultWith(
                Q(correct: true),                 // no acronyms
                Q(correct: true, "NOT_A_CODE"),   // unknown acronym
                Q(correct: false, "SB")),         // the only one that counts
        };

        var report = MasteryRollup.Compute(results, tax);

        Assert.Equal(1, report.TotalAnswered);
        Assert.Equal(0, report.TotalCorrect);
        Assert.Equal(0, report.Subtopic("3.1").Progress);
        Assert.Equal(1, report.Subtopic("3.1").Answered);
    }

    [Fact]
    public void Compute_NoData_ReturnsEmptyReport()
    {
        var report = MasteryRollup.Compute(Array.Empty<ExamResult>(), SmallTaxonomy());
        Assert.False(report.HasData);
        Assert.Equal(0, report.TotalAnswered);
        Assert.Equal(0, report.Subtopic("3.1").Progress);
    }
}
