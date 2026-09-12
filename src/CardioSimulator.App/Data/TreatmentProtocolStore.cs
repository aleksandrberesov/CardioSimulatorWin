using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardioSimulator.App.Data;

/// <summary>
/// Persists the editable Treatment Protocols set as a single JSON document at <see cref="Path"/>
/// (<c>treatment-protocols.json</c>). Mirrors the app's other single-file stores (see
/// <c>StudentStore</c>): atomic temp+move writes, relaxed escaping so Cyrillic is written literally,
/// enums as strings. A missing or unreadable file yields the built-in defaults
/// (<see cref="TreatmentProtocolDefaults"/>) so the screen is never empty; the file is only written on
/// the first actual edit (or an explicit <see cref="ResetToDefaults"/>).
/// </summary>
public sealed class TreatmentProtocolStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; }

    public TreatmentProtocolStore(string path)
    {
        Path = path;
    }

    /// <summary>Whether the user has ever saved edits (a file exists on disk).</summary>
    public bool HasSaved => File.Exists(Path);

    /// <summary>The persisted set, or the built-in defaults when nothing is saved / the file is
    /// unreadable. Never returns null and never throws.</summary>
    public TreatmentProtocolSet Load()
    {
        try
        {
            if (!File.Exists(Path)) return TreatmentProtocolDefaults.Build();
            var set = JsonSerializer.Deserialize<TreatmentProtocolSet>(File.ReadAllText(Path, Encoding.UTF8), JsonOptions);
            return set ?? TreatmentProtocolDefaults.Build();
        }
        catch
        {
            return TreatmentProtocolDefaults.Build();
        }
    }

    /// <summary>Writes the whole set atomically. Returns true on success.</summary>
    public bool Save(TreatmentProtocolSet set)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(set, JsonOptions), Utf8NoBom);
            if (File.Exists(Path)) File.Delete(Path);
            File.Move(tmp, Path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Restores the built-in defaults, persisting them, and returns the fresh set.</summary>
    public TreatmentProtocolSet ResetToDefaults()
    {
        var defaults = TreatmentProtocolDefaults.Build();
        Save(defaults);
        return defaults;
    }
}
