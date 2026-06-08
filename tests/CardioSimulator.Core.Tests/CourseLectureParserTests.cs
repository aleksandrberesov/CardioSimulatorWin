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
}
