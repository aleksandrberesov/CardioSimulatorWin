using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TestJsonTests
{
    [Fact]
    public void Test_RoundTrips_WithQuestionsOptionsAndComment()
    {
        var original = TestSeed.Sample(new[] { "ecg_a", "ecg_b", "ecg_c" });

        var json = TestJson.Serialize(original);
        var round = TestJson.Deserialize(json);

        Assert.NotNull(round);
        Assert.Equal(original.TestId, round!.TestId);
        Assert.Equal(original.Title, round.Title);
        Assert.Equal(original.QuestionTimeSeconds, round.QuestionTimeSeconds);
        Assert.Equal(original.Questions.Count, round.Questions.Count);

        for (var i = 0; i < original.Questions.Count; i++)
        {
            var a = original.Questions[i];
            var b = round.Questions[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Number, b.Number);
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(a.CorrectOptionId, b.CorrectOptionId);
            Assert.Equal(a.Comment, b.Comment);
            Assert.Equal(a.PathologyId, b.PathologyId);
            Assert.Equal(a.Options.Select(o => (o.Id, o.Text)), b.Options.Select(o => (o.Id, o.Text)));
        }
    }

    [Fact]
    public void Json_WritesCyrillicLiterally()
    {
        var json = TestJson.Serialize(TestSeed.Sample(new[] { "ecg_a" }));
        Assert.Contains("Депрессия", json);
        Assert.DoesNotContain("\\u04", json); // no escaped Cyrillic
    }

    [Fact]
    public void CorrectOptionNumber_IsOneBasedPosition()
    {
        var q = new TestQuestion(
            "q", 1, "text",
            new List<TestOption> { new("a", "A"), new("b", "B"), new("c", "C") },
            CorrectOptionId: "b",
            Comment: "");

        Assert.Equal(2, q.CorrectOptionNumber());
        Assert.Equal("B", q.CorrectOption()!.Text);
    }

    [Fact]
    public void Question_WithoutLeads_DefaultsToEmptyAndGridScheme()
    {
        var q = new TestQuestion("q", 1, "t", new List<TestOption> { new("a", "A") }, "a", "");
        Assert.Empty(q.LeadList);
        Assert.Equal(SeriesScheme.Grid, q.Scheme);
    }
}
