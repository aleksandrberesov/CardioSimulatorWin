using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Examination («Экзамен») domain types. The exam reuses the self-assessment <see cref="Test"/> bank
/// (same questions repository) but runs as a graded assessment: no per-question comments/feedback, and
/// the attempt is graded and <em>saved</em> at the end, then viewable — modeled on the OSCE result
/// pipeline (<see cref="OskeResult"/> / <see cref="OskeResultStore"/> / <see cref="OskeGrader"/>).
/// </summary>
public sealed record ExamStudentInfo(string FullName, string Group);

/// <summary>Per-question grading outcome. <see cref="Selected"/> is null when left unanswered.
/// <see cref="Acronyms"/> and <see cref="Subsection"/> capture the question's taxonomy tags and its
/// mapped course subsection at grade time so the result rolls up into subsection/section mastery
/// independently of the (possibly later edited/deleted) test.</summary>
public sealed record ExamQuestionResult(
    string QuestionId,
    string? Selected,
    string Correct,
    bool IsCorrect,
    IReadOnlyList<string>? Acronyms = null,
    string? Subsection = null)
{
    /// <summary>The captured taxonomy acronyms (never null; empty when the question was untagged).</summary>
    public IReadOnlyList<string> AcronymList => Acronyms ?? System.Array.Empty<string>();

    /// <summary>The captured course subsection key (e.g. <c>1.2</c>), or null when the question wasn't
    /// mapped to one. A direct Learning-Scale join key that works even for acronym-less theory
    /// questions — see <c>MasteryRollup</c>.</summary>
    public string? SubsectionKey => string.IsNullOrWhiteSpace(Subsection) ? null : Subsection!.Trim();
}

/// <summary>A graded exam attempt — persisted as one JSON file per attempt. <see cref="TestTitle"/> is
/// captured so the results viewer reads independently of the (possibly edited/deleted) test.</summary>
public sealed record ExamResult(
    ExamStudentInfo Student,
    string TestId,
    string TestTitle,
    System.DateTimeOffset Timestamp,
    IReadOnlyList<ExamQuestionResult> Questions,
    int CorrectCount,
    int TotalCount,
    bool Passed);
