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

    private TreatmentPresetContainer? _container;

    public string Path { get; }

    public TreatmentProtocolStore(string path)
    {
        Path = path;
    }

    /// <summary>Whether the user has ever saved edits (a file exists on disk).</summary>
    public bool HasSaved => File.Exists(Path);

    /// <summary>Loads the entire preset container, or initializes with default presets.</summary>
    public TreatmentPresetContainer LoadContainer()
    {
        if (_container is not null) return _container;
        try
        {
            if (!File.Exists(Path))
            {
                _container = CreateDefaultContainer();
                return _container;
            }

            var text = File.ReadAllText(Path, Encoding.UTF8);

            // Container format with presets
            if (text.Contains("\"presets\"", StringComparison.OrdinalIgnoreCase))
            {
                var container = JsonSerializer.Deserialize<TreatmentPresetContainer>(text, JsonOptions);
                if (container is { Presets.Count: > 0 })
                {
                    foreach (var p in container.Presets)
                    {
                        if (p.ProtocolSet is not null) RepairSeedTypos(p.ProtocolSet);
                    }
                    _container = container;
                    return _container;
                }
            }

            // Legacy format: raw TreatmentProtocolSet
            var legacySet = JsonSerializer.Deserialize<TreatmentProtocolSet>(text, JsonOptions);
            if (legacySet is not null)
            {
                RepairSeedTypos(legacySet);
                _container = new TreatmentPresetContainer
                {
                    ActivePresetId = "default",
                    Presets = new List<TreatmentProtocolPreset>
                    {
                        new TreatmentProtocolPreset
                        {
                            Id = "default",
                            Name = "Стандарт приказа № 2345 ДЗМ Москвы",
                            Description = "Стандартный клинический протокол сердечно-сосудистой реанимации",
                            IsBuiltIn = true,
                            ProtocolSet = legacySet,
                        }
                    }
                };
                return _container;
            }

            _container = CreateDefaultContainer();
            return _container;
        }
        catch
        {
            _container = CreateDefaultContainer();
            return _container;
        }
    }

    private static TreatmentPresetContainer CreateDefaultContainer()
    {
        return new TreatmentPresetContainer
        {
            ActivePresetId = "default",
            Presets = new List<TreatmentProtocolPreset>
            {
                new TreatmentProtocolPreset
                {
                    Id = "default",
                    Name = "Стандарт приказа № 2345 ДЗМ Москвы",
                    Description = "Стандартный клинический протокол сердечно-сосудистой реанимации",
                    IsBuiltIn = true,
                    ProtocolSet = TreatmentProtocolDefaults.Build(),
                }
            }
        };
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

    /// <summary>The active persisted set, or built-in defaults.</summary>
    public TreatmentProtocolSet Load() => GetActivePreset().ProtocolSet;

    /// <summary>Gets the currently active preset.</summary>
    public TreatmentProtocolPreset GetActivePreset()
    {
        var c = LoadContainer();
        var active = c.Presets.Find(p => p.Id == c.ActivePresetId);
        if (active is not null) return active;
        if (c.Presets.Count > 0)
        {
            c.ActivePresetId = c.Presets[0].Id;
            return c.Presets[0];
        }
        var def = new TreatmentProtocolPreset
        {
            Id = "default",
            Name = "Стандарт приказа № 2345 ДЗМ Москвы",
            Description = "Стандартный клинический протокол",
            IsBuiltIn = true,
            ProtocolSet = TreatmentProtocolDefaults.Build(),
        };
        c.Presets.Add(def);
        c.ActivePresetId = def.Id;
        return def;
    }

    /// <summary>Sets the active preset ID and persists the change.</summary>
    public void SetActivePresetId(string presetId)
    {
        var c = LoadContainer();
        if (c.Presets.Exists(p => p.Id == presetId))
        {
            c.ActivePresetId = presetId;
            SaveContainer(c);
        }
    }

    /// <summary>Saves the entire preset container atomically.</summary>
    public bool SaveContainer(TreatmentPresetContainer container)
    {
        try
        {
            _container = container;
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(container, JsonOptions), Utf8NoBom);
            if (File.Exists(Path)) File.Delete(Path);
            File.Move(tmp, Path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Writes the active protocol set atomically.</summary>
    public bool Save(TreatmentProtocolSet set)
    {
        var c = LoadContainer();
        var active = GetActivePreset();
        active.ProtocolSet = set;
        return SaveContainer(c);
    }

    /// <summary>Clones the given set as a new named preset and sets it as active.</summary>
    public bool SaveAsNewPreset(string name, string description, TreatmentProtocolSet set)
    {
        var c = LoadContainer();
        var newPreset = new TreatmentProtocolPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Description = description,
            IsBuiltIn = false,
            ProtocolSet = set.Clone(),
        };
        c.Presets.Add(newPreset);
        c.ActivePresetId = newPreset.Id;
        return SaveContainer(c);
    }

    /// <summary>Deletes a custom preset (cannot delete built-in or last preset).</summary>
    public bool DeletePreset(string presetId)
    {
        var c = LoadContainer();
        if (c.Presets.Count <= 1) return false;
        var preset = c.Presets.Find(p => p.Id == presetId);
        if (preset is null || preset.IsBuiltIn) return false;
        c.Presets.Remove(preset);
        if (c.ActivePresetId == presetId)
            c.ActivePresetId = c.Presets[0].Id;
        return SaveContainer(c);
    }

    /// <summary>Serializes a preset to JSON for export.</summary>
    public string ExportPresetJson(string presetId)
    {
        var c = LoadContainer();
        var preset = c.Presets.Find(p => p.Id == presetId) ?? GetActivePreset();
        return JsonSerializer.Serialize(preset, JsonOptions);
    }

    /// <summary>Imports a preset, container, or raw protocol set from JSON.</summary>
    public bool ImportPresetJson(string json, out string? newPresetId, out string? error)
    {
        newPresetId = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Empty JSON";
                return false;
            }

            var c = LoadContainer();

            if (json.Contains("\"protocolSet\"", StringComparison.OrdinalIgnoreCase))
            {
                var preset = JsonSerializer.Deserialize<TreatmentProtocolPreset>(json, JsonOptions);
                if (preset?.ProtocolSet is not null)
                {
                    preset.Id = Guid.NewGuid().ToString("N");
                    preset.IsBuiltIn = false;
                    RepairSeedTypos(preset.ProtocolSet);
                    c.Presets.Add(preset);
                    c.ActivePresetId = preset.Id;
                    SaveContainer(c);
                    newPresetId = preset.Id;
                    return true;
                }
            }

            if (json.Contains("\"presets\"", StringComparison.OrdinalIgnoreCase))
            {
                var importedContainer = JsonSerializer.Deserialize<TreatmentPresetContainer>(json, JsonOptions);
                if (importedContainer is { Presets.Count: > 0 })
                {
                    foreach (var p in importedContainer.Presets)
                    {
                        p.Id = Guid.NewGuid().ToString("N");
                        p.IsBuiltIn = false;
                        if (p.ProtocolSet is not null) RepairSeedTypos(p.ProtocolSet);
                        c.Presets.Add(p);
                    }
                    c.ActivePresetId = c.Presets[^1].Id;
                    SaveContainer(c);
                    newPresetId = c.ActivePresetId;
                    return true;
                }
            }

            var rawSet = JsonSerializer.Deserialize<TreatmentProtocolSet>(json, JsonOptions);
            if (rawSet is not null)
            {
                RepairSeedTypos(rawSet);
                var p = new TreatmentProtocolPreset
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Импортированный протокол (" + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ")",
                    IsBuiltIn = false,
                    ProtocolSet = rawSet,
                };
                c.Presets.Add(p);
                c.ActivePresetId = p.Id;
                SaveContainer(c);
                newPresetId = p.Id;
                return true;
            }

            error = "Unrecognized JSON format";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Restores the built-in defaults for the active preset and returns the fresh set.</summary>
    public TreatmentProtocolSet ResetToDefaults()
    {
        var defaults = TreatmentProtocolDefaults.Build();
        Save(defaults);
        return defaults;
    }
}
