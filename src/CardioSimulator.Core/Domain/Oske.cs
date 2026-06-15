namespace CardioSimulator.Core.Domain;

/// <summary>
/// OSCE (ОСКЭ) station domain types. The candidate records an ECG and interprets it by filling an
/// on-screen <em>conclusion form</em> (accreditation passport §15) which is auto-graded against a
/// per-ECG answer key (passport chek-list item 29 — «Сформулировал верное заключение», computer
/// graded). There are two form variants: <see cref="OskeSpecialty.Therapy"/>, and a shared form for
/// <see cref="OskeSpecialty.Cardiology"/> + <see cref="OskeSpecialty.FunctionalDiagnostics"/>.
/// </summary>
public enum OskeSpecialty
{
    Therapy,
    Cardiology,
    FunctionalDiagnostics,
}

/// <summary>Whether a question accepts one option or several («возможно несколько ответов»).</summary>
public enum OskeAnswerKind
{
    Single,
    Multi,
}

/// <summary>One selectable answer within a question. <see cref="Id"/> is a stable key; <see cref="Text"/> is the displayed Russian label.</summary>
public sealed record OskeOption(string Id, string Text);

/// <summary>One block of the conclusion form (e.g. «Ритм»).</summary>
public sealed record OskeQuestion(
    string Id,
    int Number,
    string Title,
    OskeAnswerKind Kind,
    IReadOnlyList<OskeOption> Options);

/// <summary>
/// A complete conclusion-form template for one specialty. <see cref="PassFraction"/> is the share of
/// correct blocks required to pass (default 1.0 — all blocks; tunable per the teacher's rule).
/// </summary>
public sealed record OskeForm(
    string FormId,
    OskeSpecialty Specialty,
    string Version,
    IReadOnlyList<OskeQuestion> Questions,
    double PassFraction = 1.0);

/// <summary>
/// The correct answers for one ECG under one form. <see cref="CorrectOptionIds"/> maps a question id
/// to the set of correct <see cref="OskeOption.Id"/>s (one for single-select, several for multi).
/// </summary>
public sealed record OskeAnswerKey(
    string EcgId,
    string FormId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CorrectOptionIds);

/// <summary>Candidate identity captured before the attempt (saved with the result file).</summary>
public sealed record OskeStudentInfo(string FullName, string Group);

/// <summary>Per-block grading outcome.</summary>
public sealed record OskeBlockResult(
    string QuestionId,
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Correct,
    bool IsCorrect);

/// <summary>A graded attempt — persisted as one JSON file per attempt.</summary>
public sealed record OskeResult(
    OskeStudentInfo Student,
    OskeSpecialty Specialty,
    string EcgId,
    string FormId,
    DateTimeOffset Timestamp,
    IReadOnlyList<OskeBlockResult> Blocks,
    int CorrectCount,
    int TotalCount,
    bool Passed);

/// <summary>Specialty → form-id mapping. Cardiology and Functional Diagnostics share one form.</summary>
public static class OskeForms
{
    public const string TherapyFormId = "therapy";
    public const string CardiologyFormId = "cardiology";

    public static string FormIdFor(OskeSpecialty specialty) => specialty switch
    {
        OskeSpecialty.Therapy => TherapyFormId,
        _ => CardiologyFormId,
    };
}
