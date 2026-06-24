using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TestGeneratorTests
{
    private static List<TestQuestion> Bank(int n, string? theme = null)
    {
        var list = new List<TestQuestion>(n);
        for (var i = 0; i < n; i++)
            list.Add(new TestQuestion(
                "q" + i, 0, "Question " + i,
                new List<TestOption> { new("a", "A"), new("b", "B") },
                "a", "Because A.", Theme: theme));
        return list;
    }

    [Fact]
    public void Generate_CapsToAvailable()
    {
        var test = TestGenerator.Generate(Bank(5), count: 10, rng: new Random(1));
        Assert.Equal(5, test.Questions.Count);
    }

    [Fact]
    public void Generate_TakesRequestedCount_AndRenumbers()
    {
        var test = TestGenerator.Generate(Bank(50), count: 20, rng: new Random(1));
        Assert.Equal(20, test.Questions.Count);
        Assert.Equal(Enumerable.Range(1, 20), test.Questions.Select(q => q.Number));
        Assert.Equal(20, test.Questions.Select(q => q.Id).Distinct().Count()); // no duplicates
    }

    [Fact]
    public void Generate_FiltersByTheme()
    {
        var bank = Bank(10, "Ритм").Concat(Bank(10, "Инфаркт")).ToList();
        var test = TestGenerator.Generate(bank, count: 30, theme: "Инфаркт", rng: new Random(2));
        Assert.Equal(10, test.Questions.Count);
        Assert.All(test.Questions, q => Assert.Equal("Инфаркт", q.Theme));
    }

    [Fact]
    public void Generate_IsDeterministic_ForSameSeed()
    {
        var bank = Bank(30);
        var a = TestGenerator.Generate(bank, 10, rng: new Random(42));
        var b = TestGenerator.Generate(bank, 10, rng: new Random(42));
        Assert.Equal(a.Questions.Select(q => q.Id), b.Questions.Select(q => q.Id));
    }

    [Fact]
    public void Generate_EmptyBank_YieldsNoQuestions()
    {
        var test = TestGenerator.Generate(new List<TestQuestion>(), 10, rng: new Random(1));
        Assert.Empty(test.Questions);
        Assert.StartsWith("gen_", test.TestId);
    }

    [Fact]
    public void QuizDto_OmitsAnswerKeyAndComment()
    {
        var q = new TestQuestion(
            "q1", 1, "Pick A.",
            new List<TestOption> { new("a", "Right"), new("b", "Wrong") },
            "a", "SECRET-EXPLANATION");
        var json = QuizDto.Serialize(QuizDto.ToPublic(new Test("t", "T", new[] { q })));

        Assert.DoesNotContain("correctOptionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-EXPLANATION", json);
        Assert.Contains("Pick A.", json);   // the prompt is sent
        Assert.Contains("Right", json);     // option texts are sent (the key is not)
    }

    [Fact]
    public void QuizDto_MapsStimulusKind()
    {
        Assert.Equal("image", QuizDto.ToPublic(new TestQuestion("i", 0, "?", Array.Empty<TestOption>(), "", "", ImagePath: "i.png")).Stimulus);
        Assert.Equal("ecg", QuizDto.ToPublic(new TestQuestion("e", 0, "?", Array.Empty<TestOption>(), "", "", PathologyId: "sinus")).Stimulus);
        Assert.Equal("text", QuizDto.ToPublic(new TestQuestion("t", 0, "?", Array.Empty<TestOption>(), "", "")).Stimulus);
    }
}
