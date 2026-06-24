using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// JSON (de)serialization for the self-assessment test files. Mirrors <see cref="OskeJson"/>: relaxed
/// escaping so the Russian clinical text is written literally (not <c>\uXXXX</c>), string enums for a
/// readable on-disk schema, and camelCase property names. The <see cref="Test"/> record maps 1:1 to a
/// <c>tests/&lt;testId&gt;.json</c> file.
/// </summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Test test) => JsonSerializer.Serialize(test, Options);
    public static Test? Deserialize(string json) => JsonSerializer.Deserialize<Test>(json, Options);

    /// <summary>One bank question ⇄ one <c>tests/bank/&lt;id&gt;.json</c> file.</summary>
    public static string SerializeQuestion(TestQuestion question) => JsonSerializer.Serialize(question, Options);
    public static TestQuestion? DeserializeQuestion(string json) => JsonSerializer.Deserialize<TestQuestion>(json, Options);

    /// <summary>A list of questions as a JSON array — the import/export interchange format (the schema
    /// AI-generated question batches target).</summary>
    public static string SerializeBank(IEnumerable<TestQuestion> questions) =>
        JsonSerializer.Serialize(questions, Options);
    public static IReadOnlyList<TestQuestion> DeserializeBank(string json) =>
        JsonSerializer.Deserialize<List<TestQuestion>>(json, Options) ?? new List<TestQuestion>();

    public static string SerializeExamResult(ExamResult result) => JsonSerializer.Serialize(result, Options);
    public static ExamResult? DeserializeExamResult(string json) => JsonSerializer.Deserialize<ExamResult>(json, Options);
}
