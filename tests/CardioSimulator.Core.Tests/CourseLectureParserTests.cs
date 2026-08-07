using System.Collections.Generic;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class CourseLectureParserTests
{
    [Fact]
    public void ParseLecture_KeepsRawHtmlBodyVerbatim()
    {
        const string text =
            "---\nid: intro\norder: 1\ntitle: Intro\nschemaVersion: 1\n---\n<h1>Hi</h1>\n<p>Body</p>";

        var lecture = CourseParser.ParseLecture(text, "c1", "en");

        Assert.Equal("intro", lecture.Id);
        Assert.Equal("c1", lecture.CourseId);
        Assert.Equal("en", lecture.Language);
        Assert.Equal("Intro", lecture.FrontMatter.Title);
        Assert.Equal("<h1>Hi</h1>\n<p>Body</p>", lecture.RawHtml);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsFrontMatterAndBody()
    {
        var fm = new LectureFrontMatter("intro", 2, "Intro", 1, new Dictionary<string, string>());
        var lecture = new Lecture("intro", "c1", "en", fm, "<p>Hello</p>");

        var round = CourseParser.ParseLecture(CourseParser.SerializeLecture(lecture), "c1", "en");

        Assert.Equal("intro", round.Id);
        Assert.Equal(2, round.FrontMatter.Order);
        Assert.Equal("Intro", round.FrontMatter.Title);
        Assert.Equal("<p>Hello</p>", round.RawHtml);
    }

    [Fact]
    public void SerializeThenParse_Course_RoundTripsTopicsAndSubtopics()
    {
        var course = new Course(
            Id: "c1",
            TitleEn: "Course One",
            NameRu: null,
            Authors: null,
            Languages: new[] { "en" },
            Lectures: new[]
            {
                new LectureEntry("intro", "Intro", null),               // ungrouped
                new LectureEntry("afib", "AFib", null, "arrhythmias"),  // under a topic
                new LectureEntry("vt", "VT", null, "arrhythmias"),
            },
            Pathologies: System.Array.Empty<string>(),
            Topics: new[]
            {
                new TopicEntry("arrhythmias", "Arrhythmias", "Аритмии"),
                new TopicEntry("blocks", "Blocks", null),               // topic with no subtopics
            });

        var round = CourseParser.ParseCourse(CourseParser.SerializeCourse(course));

        Assert.Equal(2, round.Topics.Count);
        Assert.Equal("arrhythmias", round.Topics[0].Id);
        Assert.Equal("Аритмии", round.Topics[0].NameRu);
        Assert.Equal("blocks", round.Topics[1].Id);          // empty topic survives the round-trip
        Assert.Equal(3, round.Lectures.Count);
        Assert.Null(round.Lectures[0].Topic);                 // ungrouped stays ungrouped
        Assert.Equal("arrhythmias", round.Lectures[1].Topic);
        Assert.Equal("arrhythmias", round.Lectures[2].Topic);
    }

    [Fact]
    public void SerializeThenParse_Course_RoundTripsLeafTopic()
    {
        // A course mixing both shapes: a group Тема with a Подтема, and a content-bearing leaf Тема.
        var course = new Course(
            Id: "c1",
            TitleEn: "Course One",
            NameRu: null,
            Authors: null,
            Languages: new[] { "en" },
            Lectures: new[] { new LectureEntry("afib", "AFib", null, "arrhythmias") },
            Pathologies: System.Array.Empty<string>(),
            Topics: new[]
            {
                new TopicEntry("arrhythmias", "Arrhythmias", null),            // group
                new TopicEntry("overview", "Overview", "Обзор", IsLeaf: true), // leaf (Course → Тема)
            });

        var text = CourseParser.SerializeCourse(course);
        Assert.Contains("topic:overview;title:Overview;name:Обзор;leaf:true", text);
        Assert.DoesNotContain("topic:arrhythmias;title:Arrhythmias;name", text); // group has no leaf flag

        var round = CourseParser.ParseCourse(text);
        Assert.False(round.Topics.Single(t => t.Id == "arrhythmias").IsLeaf);
        Assert.True(round.Topics.Single(t => t.Id == "overview").IsLeaf);
    }

    [Fact]
    public void ContentItem_ResolvesLeafTopicAndSubtopic_ButNotGroup()
    {
        var course = new Course(
            Id: "c1", TitleEn: "C1", NameRu: null, Authors: null,
            Languages: new[] { "en" },
            Lectures: new[] { new LectureEntry("afib", "AFib", null, "arrhythmias") },
            Pathologies: System.Array.Empty<string>(),
            Topics: new[]
            {
                new TopicEntry("arrhythmias", "Arrhythmias", null),            // group → not a content item
                new TopicEntry("overview", "Overview", "Обзор", IsLeaf: true), // leaf → a content item
            });

        Assert.Equal("afib", course.ContentItem("afib")?.Id);          // Подтема
        var leaf = course.ContentItem("overview");
        Assert.NotNull(leaf);
        Assert.Equal("overview", leaf!.Topic);                          // leaf points its Topic at itself
        Assert.Equal("Обзор", leaf.NameRu);
        Assert.Null(course.ContentItem("arrhythmias"));                 // a group Тема is not clickable content
    }

    [Fact]
    public void FirstContentItemId_PrefersUngrouped_ThenLeafOrGroupMember()
    {
        var ungroupedFirst = new Course(
            "c", "C", null, null, new[] { "en" },
            new[] { new LectureEntry("intro", "Intro", null) },
            System.Array.Empty<string>(),
            new[] { new TopicEntry("t", "T", null, IsLeaf: true) });
        Assert.Equal("intro", ungroupedFirst.FirstContentItemId());

        var leafOnly = new Course(
            "c", "C", null, null, new[] { "en" },
            System.Array.Empty<LectureEntry>(),
            System.Array.Empty<string>(),
            new[] { new TopicEntry("overview", "Overview", null, IsLeaf: true) });
        Assert.Equal("overview", leafOnly.FirstContentItemId());        // course with only a leaf Тема

        var groupMember = new Course(
            "c", "C", null, null, new[] { "en" },
            new[] { new LectureEntry("afib", "AFib", null, "arr") },
            System.Array.Empty<string>(),
            new[] { new TopicEntry("arr", "Arrhythmias", null) });
        Assert.Equal("afib", groupMember.FirstContentItemId());         // first Подтема of the group
    }

    [Fact]
    public void ParseCourse_LegacyWithoutTopics_YieldsUngroupedLectures()
    {
        // A course.txt authored before topics existed: only "lecture:" lines, no "topic:" lines.
        const string text =
            "course:c1\ntitle:Course One\nlanguage:en\n\nlecture:intro;title:Intro\nlecture:basics;title:Basics\n";

        var course = CourseParser.ParseCourse(text);

        Assert.Empty(course.Topics);
        Assert.Equal(2, course.Lectures.Count);
        Assert.All(course.Lectures, l => Assert.Null(l.Topic));
    }

    [Fact]
    public void Lecture_IsStandalone_ReflectsLayoutExtra()
    {
        var plain = new Lecture("a", "c", "en",
            new LectureFrontMatter("a", 0, "A", 1, new Dictionary<string, string>()), "<p>x</p>");
        Assert.False(plain.IsStandalone);

        var standalone = new Lecture("a", "c", "en",
            new LectureFrontMatter("a", 0, "A", 1, new Dictionary<string, string> { ["layout"] = "standalone" }),
            "<!DOCTYPE html><html><body>x</body></html>");
        Assert.True(standalone.IsStandalone);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsStandaloneLayoutExtra()
    {
        var fm = new LectureFrontMatter("intro", 0, "Intro", 1,
            new Dictionary<string, string> { ["layout"] = "standalone" });
        var lecture = new Lecture("intro", "c1", "en", fm,
            "<!DOCTYPE html><html><head></head><body><h1>Doc</h1></body></html>");

        var round = CourseParser.ParseLecture(CourseParser.SerializeLecture(lecture), "c1", "en");

        Assert.True(round.IsStandalone);
        Assert.Equal(lecture.RawHtml, round.RawHtml);
    }

    private static Lecture LectureWith(string html, bool standaloneFlag)
    {
        var extras = standaloneFlag
            ? new Dictionary<string, string> { ["layout"] = "standalone" }
            : new Dictionary<string, string>();
        return new Lecture("a", "c", "en", new LectureFrontMatter("a", 0, "A", 1, extras), html);
    }

    [Fact]
    public void WithReconciledLayout_ClearsFlag_WhenContentBecomesFragment()
    {
        // The reported bug: a standalone page decomposed into a fragment keeps a stale flag.
        var reconciled = LectureWith("<div class=\"lecture-embed\">…</div>", standaloneFlag: true)
            .WithReconciledLayout();
        Assert.False(reconciled.IsStandalone);
        Assert.False(reconciled.FrontMatter.Extras.ContainsKey("layout"));
    }

    [Fact]
    public void WithReconciledLayout_SetsFlag_WhenContentIsFullDocument()
    {
        var reconciled = LectureWith("<!DOCTYPE html><html><body>x</body></html>", standaloneFlag: false)
            .WithReconciledLayout();
        Assert.True(reconciled.IsStandalone);
    }

    [Fact]
    public void WithReconciledLayout_NoOp_WhenAlreadyConsistent()
    {
        var frag = LectureWith("<p>x</p>", standaloneFlag: false);
        Assert.Same(frag, frag.WithReconciledLayout());

        var full = LectureWith("<!DOCTYPE html><html><body>x</body></html>", standaloneFlag: true);
        Assert.Same(full, full.WithReconciledLayout());
    }
}
