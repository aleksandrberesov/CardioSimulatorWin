using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class OskeGraderTests
{
    private static readonly OskeStudentInfo Student = new("Иванов Иван Иванович", "Группа-1");

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Sel(
        params (string q, string[] opts)[] entries)
    {
        var d = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (q, opts) in entries) d[q] = opts;
        return d;
    }

    private static OskeForm TinyForm(double passFraction = 1.0) => new(
        "tiny", OskeSpecialty.Therapy, "test",
        new[]
        {
            new OskeQuestion("a", 1, "A", OskeAnswerKind.Single, new[] { new OskeOption("x", "X"), new OskeOption("y", "Y") }),
            new OskeQuestion("b", 2, "B", OskeAnswerKind.Single, new[] { new OskeOption("x", "X"), new OskeOption("y", "Y") }),
            new OskeQuestion("c", 3, "C", OskeAnswerKind.Multi, new[] { new OskeOption("p", "P"), new OskeOption("q", "Q"), new OskeOption("r", "R") }),
        },
        passFraction);

    private static bool BlockCorrect(OskeResult r, string questionId) =>
        r.Blocks.Single(b => b.QuestionId == questionId).IsCorrect;

    [Fact]
    public void Grade_AllCorrect_PassesWithFullScore()
    {
        var form = TinyForm();
        var key = new OskeAnswerKey("ecg1", "tiny", Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p", "r" })));
        var selections = Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p", "r" }));

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.Equal(3, result.CorrectCount);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.Passed);
        Assert.Equal("Иванов Иван Иванович", result.Student.FullName);
        Assert.All(result.Blocks, b => Assert.True(b.IsCorrect));
    }

    [Fact]
    public void Grade_OneWrong_FailsAtDefaultThreshold()
    {
        var form = TinyForm();
        var key = new OskeAnswerKey("ecg1", "tiny", Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p" })));
        var selections = Sel(("a", new[] { "x" }), ("b", new[] { "x" }), ("c", new[] { "p" })); // b wrong

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.Equal(2, result.CorrectCount);
        Assert.False(result.Passed);
        Assert.False(BlockCorrect(result, "b"));
    }

    [Fact]
    public void Grade_Multi_IsOrderIndependent()
    {
        var form = TinyForm();
        var key = new OskeAnswerKey("ecg1", "tiny",
            Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p", "r" })));
        var selections = Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "r", "p" }));

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.True(BlockCorrect(result, "c"));
        Assert.True(result.Passed);
    }

    [Fact]
    public void Grade_Multi_PartialSelection_IsWrong()
    {
        var form = TinyForm();
        var key = new OskeAnswerKey("ecg1", "tiny", Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p", "r" })));
        var selections = Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p" })); // missing r

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.Equal(2, result.CorrectCount);
        Assert.False(BlockCorrect(result, "c"));
    }

    [Fact]
    public void Grade_PassFractionThreshold_PassesWhenMet()
    {
        var form = TinyForm(passFraction: 0.6); // 2 of 3 = 0.67 ≥ 0.6
        var key = new OskeAnswerKey("ecg1", "tiny", Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p" })));
        var selections = Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "q" })); // c wrong

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.Equal(2, result.CorrectCount);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Grade_UnansweredBlock_CountsAsWrongWhenKeyExpectsAnswer()
    {
        var form = TinyForm();
        var key = new OskeAnswerKey("ecg1", "tiny", Sel(("a", new[] { "x" }), ("b", new[] { "y" }), ("c", new[] { "p" })));
        var selections = Sel(("a", new[] { "x" })); // b and c left blank

        var result = OskeGrader.Grade(form, key, selections, Student, "ecg1");

        Assert.Equal(1, result.CorrectCount);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Grade_RealSeedForm_GradesEveryBlock()
    {
        var form = OskeSeedForms.Therapy();
        // Build a key + matching selections for every block (first option of each).
        var key = new Dictionary<string, IReadOnlyList<string>>();
        var selections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var q in form.Questions)
        {
            var first = new[] { q.Options[0].Id };
            key[q.Id] = first;
            selections[q.Id] = first;
        }

        var result = OskeGrader.Grade(form, new OskeAnswerKey("ecgX", form.FormId, key), selections, Student, "ecgX");

        Assert.Equal(form.Questions.Count, result.TotalCount);
        Assert.Equal(form.Questions.Count, result.CorrectCount);
        Assert.True(result.Passed);
    }
}
