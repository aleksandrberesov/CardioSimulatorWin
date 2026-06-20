using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Auto-grades an examination attempt: each question is correct iff the student's single selection
/// equals the question's <see cref="TestQuestion.CorrectOptionId"/> (an unanswered question is
/// incorrect). Score is correct / total; the attempt passes when that fraction meets
/// <see cref="PassFraction"/>. Pure (no IO), so it is fully unit-testable. Mirrors
/// <see cref="OskeGrader"/>.
/// </summary>
public static class ExamGrader
{
    /// <summary>Fraction of correct answers required to pass (60%).</summary>
    public const double PassFraction = 0.6;

    public static ExamResult Grade(
        Test test,
        IReadOnlyDictionary<string, string> studentSelections,
        ExamStudentInfo student,
        DateTimeOffset? timestamp = null)
    {
        var results = new List<ExamQuestionResult>(test.Questions.Count);
        foreach (var q in test.Questions)
        {
            var selected = studentSelections.TryGetValue(q.Id, out var s) ? s : null;
            var correct = selected is not null && selected == q.CorrectOptionId;
            results.Add(new ExamQuestionResult(q.Id, selected, q.CorrectOptionId, correct));
        }

        var correctCount = results.Count(r => r.IsCorrect);
        var total = results.Count;
        var passed = total > 0 && (double)correctCount / total >= PassFraction;

        return new ExamResult(
            student, test.TestId, test.Title,
            timestamp ?? DateTimeOffset.Now, results, correctCount, total, passed);
    }
}
