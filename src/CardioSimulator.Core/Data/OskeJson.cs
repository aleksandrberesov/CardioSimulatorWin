using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// JSON (de)serialization for the OSCE file formats — form templates, per-ECG answer keys, and saved
/// results. Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the Russian clinical
/// text is written literally (not <c>\uXXXX</c>-escaped), and string enums for a stable, readable
/// on-disk schema. The records in <see cref="CardioSimulator.Core.Domain"/> map 1:1 to the files.
/// </summary>
public static class OskeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeForm(OskeForm form) => JsonSerializer.Serialize(form, Options);
    public static OskeForm? DeserializeForm(string json) => JsonSerializer.Deserialize<OskeForm>(json, Options);

    public static string SerializeAnswerKey(OskeAnswerKey key) => JsonSerializer.Serialize(key, Options);
    public static OskeAnswerKey? DeserializeAnswerKey(string json) => JsonSerializer.Deserialize<OskeAnswerKey>(json, Options);

    public static string SerializeResult(OskeResult result) => JsonSerializer.Serialize(result, Options);
    public static OskeResult? DeserializeResult(string json) => JsonSerializer.Deserialize<OskeResult>(json, Options);
}
