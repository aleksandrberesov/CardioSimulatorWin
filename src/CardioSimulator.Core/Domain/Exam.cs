using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Examination («Экзамен») domain types. The exam reuses the self-assessment <see cref="Test"/> bank
/// (same questions repository) but runs as a graded assessment: no per-question comments/feedback, and
/// the attempt is graded and <em>saved</em> at the end, then viewable — modeled on the OSCE result
/// pipeline (<see cref="OskeResult"/> / <see cref="OskeResultStore"/> / <see cref="OskeGrader"/>).
/// </summary>
public sealed record ExamStudentInfo(string FullName, string Group);

/// <summary>Per-question grading outcome. <see cref="Selected"/> is null when left unanswered.</summary>
public sealed record ExamQuestionResult(
    string QuestionId,
    string? Selected,
    string Correct,
    bool IsCorrect);

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
