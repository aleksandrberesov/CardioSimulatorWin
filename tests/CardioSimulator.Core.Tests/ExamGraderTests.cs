using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class ExamGraderTests
{
    private static Test SampleTest() => TestSeed.Sample(new[] { "ecg_a", "ecg_b", "ecg_c" });

    [Fact]
    public void Grade_AllCorrect_Passes()
    {
        var test = SampleTest();
        var selections = test.Questions.ToDictionary(q => q.Id, q => q.CorrectOptionId);

        var result = ExamGrader.Grade(test, selections, new ExamStudentInfo("Иванов И.И.", "К-101"));

        Assert.Equal(test.Questions.Count, result.CorrectCount);
        Assert.Equal(test.Questions.Count, result.TotalCount);
        Assert.True(result.Passed);
        Assert.All(result.Questions, b => Assert.True(b.IsCorrect));
        Assert.Equal(test.TestId, result.TestId);
        Assert.Equal(test.Title, result.TestTitle);
    }

    [Fact]
    public void Grade_Unanswered_IsIncorrect_AndBelowThresholdFails()
    {
        var test = SampleTest();
        var selections = new Dictionary<string, string>(); // nothing answered

        var result = ExamGrader.Grade(test, selections, new ExamStudentInfo("X", "Y"));

        Assert.Equal(0, result.CorrectCount);
        Assert.False(result.Passed);
        Assert.All(result.Questions, b => { Assert.Null(b.Selected); Assert.False(b.IsCorrect); });
    }

    [Fact]
    public void Grade_WrongPick_IsRecordedAndIncorrect()
    {
        var test = SampleTest();
        var q0 = test.Questions[0];
        var wrong = q0.Options.First(o => o.Id != q0.CorrectOptionId).Id;
        var selections = new Dictionary<string, string> { [q0.Id] = wrong };

        var result = ExamGrader.Grade(test, selections, new ExamStudentInfo("X", "Y"));

        var block = result.Questions.Single(b => b.QuestionId == q0.Id);
        Assert.Equal(wrong, block.Selected);
        Assert.Equal(q0.CorrectOptionId, block.Correct);
        Assert.False(block.IsCorrect);
    }
}

public class ExamResultStoreTests : IDisposable
{
    private readonly string _dir;

    public ExamResultStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "exam_res_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_Then_List_RoundTrips_NewestFirst()
    {
        var store = new ExamResultStore(_dir);
        var test = TestSeed.Sample(new[] { "ecg_a", "ecg_b", "ecg_c" });
        var selections = test.Questions.ToDictionary(q => q.Id, q => q.CorrectOptionId);

        var older = ExamGrader.Grade(test, selections, new ExamStudentInfo("Петров П.П.", "К-205"),
            new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero));
        var newer = ExamGrader.Grade(test, new Dictionary<string, string>(), new ExamStudentInfo("Сидоров С.С.", "К-206"),
            new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero));

        Assert.True(store.Save(older));
        Assert.True(store.Save(newer));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("Сидоров С.С.", list[0].Student.FullName); // newest first
        Assert.Equal("Петров П.П.", list[1].Student.FullName);
        Assert.Equal(test.Title, list[0].TestTitle);
        Assert.True(list[1].Passed);
        Assert.False(list[0].Passed);
    }
}
