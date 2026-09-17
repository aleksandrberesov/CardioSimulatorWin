using System;
using System.Collections.Generic;
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
            if (set is null) return TreatmentProtocolDefaults.Build();
            RepairSeedTypos(set);
            return set;
        }
        catch
        {
            return TreatmentProtocolDefaults.Build();
        }
    }

    /// <summary>Russian seed texts later corrected in <see cref="TreatmentProtocolDefaults"/> (untranslated
    /// English left over from the source mock-up): old exact value → fixed value.</summary>
    private static readonly Dictionary<string, string> RuSeedFixes = new()
    {
        ["Если ФЖ persists"] = "Если ФЖ сохраняется",
        ["ФЖ/бЖТ, асистолия, PEA"] = "ФЖ/бЖТ, асистолия, ЭМД/ЭБПА",
        ["в/в быстро + flush"] = "в/в быстро + промыть физраствором",
    };

    /// <summary>Sets saved before a seed text was corrected keep the old copy, so swap it for the fixed one —
    /// only where it is still exactly the old seed value, never touching text the Admin rewrote. Applied in
    /// memory; the next edit persists it.</summary>
    private static void RepairSeedTypos(TreatmentProtocolSet set)
    {
        if (set.AclsSteps is not null)
            foreach (var step in set.AclsSteps) FixRu(step.Subtitle);
        if (set.Dosages is not null)
            foreach (var dosage in set.Dosages) { FixRu(dosage.Indication); FixRu(dosage.Route); }
    }

    private static void FixRu(LocText? text)
    {
        if (text?.Ru is not null && RuSeedFixes.TryGetValue(text.Ru, out var fixedRu)) text.Ru = fixedRu;
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
