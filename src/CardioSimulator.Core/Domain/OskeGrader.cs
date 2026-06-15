using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Auto-grades a filled OSCE conclusion form against an answer key. A block is correct iff the set of
/// selected option ids equals the key's set — this covers both single-select (one id) and
/// multi-select («возможно несколько ответов») blocks. Score is correct blocks / total; the attempt
/// passes when that fraction meets <see cref="OskeForm.PassFraction"/>. Pure (no IO), so it is fully
/// unit-testable. Mirrors the passport's automatic conclusion grading.
/// </summary>
public static class OskeGrader
{
    public static OskeResult Grade(
        OskeForm form,
        OskeAnswerKey key,
        IReadOnlyDictionary<string, IReadOnlyList<string>> studentSelections,
        OskeStudentInfo student,
        string ecgId,
        DateTimeOffset? timestamp = null)
    {
        var blocks = new List<OskeBlockResult>(form.Questions.Count);
        foreach (var q in form.Questions)
        {
            var selected = studentSelections.TryGetValue(q.Id, out var s) ? s : Array.Empty<string>();
            var correct = key.CorrectOptionIds.TryGetValue(q.Id, out var c) ? c : Array.Empty<string>();
            blocks.Add(new OskeBlockResult(q.Id, selected.ToList(), correct.ToList(), SetEquals(selected, correct)));
        }

        var correctCount = blocks.Count(b => b.IsCorrect);
        var total = blocks.Count;
        var passed = total > 0 && (double)correctCount / total >= form.PassFraction;

        return new OskeResult(
            student, form.Specialty, ecgId, form.FormId,
            timestamp ?? DateTimeOffset.Now, blocks, correctCount, total, passed);
    }

    private static bool SetEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        return setA.SetEquals(b);
    }
}
