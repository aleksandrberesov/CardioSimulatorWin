using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Self-assessment («Тестирование») domain types. A <see cref="Test"/> is an ordered list of
/// single-choice <see cref="TestQuestion"/>s with <em>immediate</em> feedback: the student picks one
/// option, it is graded on the spot, and the question's <see cref="TestQuestion.Comment"/> (the
/// correct answer + an explanation) is revealed. Each question is tied to an ECG — its
/// <see cref="TestQuestion.PathologyId"/> (with optional handpicked
/// <see cref="TestQuestion.Leads"/> / <see cref="TestQuestion.Scheme"/>) is loaded onto the monitor
/// while the question shows, so the student reads the trace before answering.
/// </summary>
/// <remarks>
/// Net-new on both platforms (Android's Testing screen is a placeholder). Persisted as one JSON file
/// per test under the tests folder, authored by the Test Constructor — mirroring the OSCE
/// (<see cref="OskeForm"/>) and course pipelines.
/// </remarks>
public sealed record TestOption(string Id, string Text);

/// <summary>
/// The visual stimulus a <see cref="TestQuestion"/> shows alongside its text: nothing (text-only), a
/// picture (<see cref="TestQuestion.ImagePath"/>), or a live ECG trace
/// (<see cref="TestQuestion.PathologyId"/>). Derived, never stored — see
/// <see cref="TestQuestion.Stimulus"/>.
/// </summary>
public enum QuestionStimulus
{
    Text,
    Image,
    Ecg,
}

/// <summary>
/// One multiple-choice question. The student selects a single option; <see cref="CorrectOptionId"/>
/// is the key. <see cref="Comment"/> is the explanation shown after answering (the prototype's
/// «Комментарий»). The stimulus is one of: text-only, an <see cref="ImagePath"/> picture, or an ECG
/// driven by <see cref="PathologyId"/> (an empty/missing <see cref="Leads"/> list shows the canonical
/// first-12 leads). <see cref="Theme"/> + <see cref="Tags"/> classify the question for the bank.
/// </summary>
public sealed record TestQuestion(
    string Id,
    int Number,
    string Text,
    IReadOnlyList<TestOption> Options,
    string CorrectOptionId,
    string Comment,
    string? PathologyId = null,
    IReadOnlyList<Lead>? Leads = null,
    SeriesScheme Scheme = SeriesScheme.Grid,
    string? ImagePath = null,
    string? Theme = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>The handpicked leads (never null); empty ⇒ the canonical first-12.</summary>
    public IReadOnlyList<Lead> LeadList => Leads ?? System.Array.Empty<Lead>();

    /// <summary>The classification tags (never null).</summary>
    public IReadOnlyList<string> TagList => Tags ?? System.Array.Empty<string>();

    /// <summary>Which stimulus to show. An image takes precedence over an ECG when (unexpectedly) both
    /// are set; absent both, the question is text-only.</summary>
    public QuestionStimulus Stimulus =>
        !string.IsNullOrWhiteSpace(ImagePath) ? QuestionStimulus.Image
        : !string.IsNullOrWhiteSpace(PathologyId) ? QuestionStimulus.Ecg
        : QuestionStimulus.Text;

    /// <summary>1-based position of the correct option (the prototype's «Правильный ответ: N»), or 0
    /// if the key does not match any option.</summary>
    public int CorrectOptionNumber()
    {
        for (var i = 0; i < Options.Count; i++)
            if (Options[i].Id == CorrectOptionId) return i + 1;
        return 0;
    }

    /// <summary>The correct option, or null when the key is unset/mismatched.</summary>
    public TestOption? CorrectOption() => Options.FirstOrDefault(o => o.Id == CorrectOptionId);
}

/// <summary>
/// A self-assessment test. <see cref="QuestionTimeSeconds"/> is the per-question countdown shown next
/// to the question counter (0 = untimed; on expiry the question is revealed as unanswered).
/// </summary>
public sealed record Test(
    string TestId,
    string Title,
    IReadOnlyList<TestQuestion> Questions,
    int QuestionTimeSeconds = 0);
