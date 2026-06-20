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

    public static string SerializeExamResult(ExamResult result) => JsonSerializer.Serialize(result, Options);
    public static ExamResult? DeserializeExamResult(string json) => JsonSerializer.Deserialize<ExamResult>(json, Options);
}
