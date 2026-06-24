using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// The public, answer-free projection of a generated test that is sent to student devices in Group
/// mode. It deliberately carries <em>no</em> <see cref="TestQuestion.CorrectOptionId"/> and no
/// <see cref="TestQuestion.Comment"/> — the grading key never leaves the teacher's machine. Image
/// stimuli are fetched separately by id (so internal filenames aren't exposed); ECG stimuli degrade to
/// text on phones (no Win2D trace there).
/// </summary>
public sealed record PublicOption(string Id, string Text);

public sealed record PublicQuestion(
    string Id,
    string Text,
    string Stimulus,
    IReadOnlyList<PublicOption> Options);

public static class QuizDto
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PublicQuestion ToPublic(TestQuestion q) => new(
        q.Id,
        q.Text,
        q.Stimulus switch
        {
            QuestionStimulus.Image => "image",
            QuestionStimulus.Ecg => "ecg",
            _ => "text",
        },
        q.Options.Select(o => new PublicOption(o.Id, o.Text)).ToList());

    public static IReadOnlyList<PublicQuestion> ToPublic(Test test) =>
        test.Questions.Select(ToPublic).ToList();

    public static string Serialize(IReadOnlyList<PublicQuestion> questions) =>
        JsonSerializer.Serialize(questions, Options);
}
