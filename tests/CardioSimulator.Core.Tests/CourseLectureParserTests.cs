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
}
