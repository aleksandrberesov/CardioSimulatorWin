using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Locks the on-disk/wire formats for the acronym-taxonomy wiring: the pathology <c>acronym:</c> field
/// (dat header + manifest), the test/exam question acronyms (JSON), the captured result acronyms, and
/// the course <c>subsection:</c> link. These are the join keys the mastery rollup depends on.
/// </summary>
public class AcronymWiringTests
{
    // ── Rhythm (.dat header + manifest) ─────────────────────────────────────

    [Fact]
    public void PathologyDat_RoundTripsAcronym()
    {
        var text = string.Join("\n",
            "pathology:test1", "title:Test", "name:Тест", "acronym:AFIB", "leads:1",
            "", "lead:II", "count:3", "points:1024,1025,1026");

        var parsed = PathologyParser.ParsePathology(text);
        Assert.Equal(new[] { "AFIB" }, parsed.AcronymList);

        var reSerialized = PathologyParser.SerializePathology(parsed, Leads.All);
        Assert.Contains("acronym:AFIB", reSerialized);
        Assert.Equal(new[] { "AFIB" }, PathologyParser.ParsePathology(reSerialized).AcronymList);
    }

    [Fact]
    public void PathologyDat_RoundTripsMultipleAcronyms_PrimaryFirst()
    {
        var text = string.Join("\n",
            "pathology:multi", "title:Multi", "acronym:SB,LVH,TWC", "leads:1",
            "", "lead:II", "count:2", "points:1024,1024");

        var parsed = PathologyParser.ParsePathology(text);
        Assert.Equal(new[] { "SB", "LVH", "TWC" }, parsed.AcronymList); // order preserved

        var reSerialized = PathologyParser.SerializePathology(parsed, Leads.All);
        Assert.Contains("acronym:SB,LVH,TWC", reSerialized);
        Assert.Equal(new[] { "SB", "LVH", "TWC" }, PathologyParser.ParsePathology(reSerialized).AcronymList);
    }

    [Fact]
    public void PathologyManifest_RoundTripsMultipleAcronyms()
    {
        var text = string.Join("\n",
            "version:1.0", "baseline:1024",
            "lead_order:I,II,III,aVR,aVL,aVF,V1,V2,V3,V4,V5,V6", "pathologies:1", "",
            "pathology:test1;leads:12;title:T;name:Тест;group:hypertrophy;acronym:SB,LVH");

        var manifest = PathologyParser.ParseManifest(text);
        Assert.Equal(new[] { "SB", "LVH" }, manifest.Entries[0].AcronymList);

        var reSerialized = PathologyParser.SerializeManifest(manifest);
        Assert.Contains(";acronym:SB,LVH", reSerialized);
        Assert.Equal(new[] { "SB", "LVH" }, PathologyParser.ParseManifest(reSerialized).Entries[0].AcronymList);
    }

    [Fact]
    public void PathologyDat_WithoutAcronym_IsEmpty_AndOmitted()
    {
        var text = string.Join("\n",
            "pathology:legacy", "title:Legacy", "leads:1", "", "lead:II", "count:2", "points:1024,1024");
        var parsed = PathologyParser.ParsePathology(text);
        Assert.Empty(parsed.AcronymList);
        Assert.DoesNotContain("acronym:", PathologyParser.SerializePathology(parsed, Leads.All));
    }

    // ── Question / result acronyms (JSON) ───────────────────────────────────

    [Fact]
    public void TestQuestion_RoundTripsAcronyms()
    {
        var q = new TestQuestion(
            "q1", 1, "Ритм?",
            new[] { new TestOption("a", "ФП"), new TestOption("b", "СР") },
            "a", "…", Acronyms: new[] { "AFIB", "SR" });
        var test = new Test("t1", "T", new[] { q });

        var round = TestJson.Deserialize(TestJson.Serialize(test))!;
        Assert.Equal(new[] { "AFIB", "SR" }, round.Questions[0].AcronymList);
    }

    [Fact]
    public void TestQuestion_NoAcronyms_OmitsField()
    {
        var q = new TestQuestion("q1", 1, "T",
            new[] { new TestOption("a", "x") }, "a", "c");
        var json = TestJson.SerializeQuestion(q);
        Assert.DoesNotContain("acronyms", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TestJson.DeserializeQuestion(json)!.AcronymList);
    }

    [Fact]
    public void ExamResult_RoundTripsCapturedAcronyms()
    {
        var result = new ExamResult(
            new ExamStudentInfo("Иванов", "К-1"), "t1", "T", DateTimeOffset.UnixEpoch,
            new[] { new ExamQuestionResult("q1", "a", "a", true, new[] { "AFIB" }) },
            1, 1, true);

        var round = TestJson.DeserializeExamResult(TestJson.SerializeExamResult(result))!;
        Assert.Equal(new[] { "AFIB" }, round.Questions[0].AcronymList);
    }

    [Fact]
    public void ExamGrader_CapturesQuestionAcronyms()
    {
        var q = new TestQuestion("q1", 1, "T",
            new[] { new TestOption("a", "x"), new TestOption("b", "y") },
            "a", "c", Acronyms: new[] { "AFIB" });
        var test = new Test("t1", "T", new[] { q });

        var result = ExamGrader.Grade(test,
            new Dictionary<string, string> { ["q1"] = "a" },
            new ExamStudentInfo("X", "Y"));

        Assert.Equal(new[] { "AFIB" }, result.Questions[0].AcronymList);
    }

    // ── Course subsection link ──────────────────────────────────────────────

    [Fact]
    public void Course_RoundTripsLectureAndTopicSubsection()
    {
        var text = string.Join("\n",
            "course:c1", "title:C", "",
            "topic:t-av;title:AV;name:АВ;subsection:4.6",
            "lecture:l1;title:Brady;name:Бради;subsection:3.1.2");

        var course = CourseParser.ParseCourse(text);
        Assert.Equal("4.6", course.Topics.Single().Subsection);
        Assert.Equal("3.1.2", course.Lectures.Single().Subsection);

        var round = CourseParser.ParseCourse(CourseParser.SerializeCourse(course));
        Assert.Equal("4.6", round.Topics.Single().Subsection);
        Assert.Equal("3.1.2", round.Lectures.Single().Subsection);
    }
}
