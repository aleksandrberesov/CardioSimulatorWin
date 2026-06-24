using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CardioSimulator.Core.Data;

/// <summary>
/// The editable theme catalog for the question bank — a plain ordered list of theme / section /
/// lecture-block names persisted as a JSON string array at <c>tests/bank/themes.json</c>. Kept
/// deliberately separate from the questions (the customer asked for the theme list to be managed on
/// its own and extended by the user). Display order = file order; writes are atomic.
/// </summary>
public sealed class TestThemeStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Seeded on first run when no catalog exists; the user edits from here.</summary>
    public static readonly IReadOnlyList<string> DefaultThemes = new[]
    {
        "Нарушения ритма",
        "Нарушения проводимости",
        "Инфаркт миокарда",
        "Ишемия миокарда",
        "Гипертрофия",
        "Электролитные нарушения",
        "ЭКГ-синдромы",
        "Основы ЭКГ",
    };

    private readonly string _path;

    public TestThemeStore(string path)
    {
        _path = path;
    }

    /// <summary>The catalog (file order). Empty when none is on disk / unreadable.</summary>
    public IReadOnlyList<string> Read()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<string>();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path, Encoding.UTF8));
            return list?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Overwrites the catalog, de-duplicating (case-insensitive) while keeping first order.</summary>
    public bool Write(IEnumerable<string> themes)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var cleaned = new List<string>();
        foreach (var raw in themes)
        {
            var t = raw?.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t)) cleaned.Add(t);
        }
        return AtomicWriteText(JsonSerializer.Serialize(cleaned, JsonOptions));
    }

    /// <summary>Adds a theme if absent; returns true when it was actually added.</summary>
    public bool Add(string theme)
    {
        theme = theme?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(theme)) return false;
        var list = Read().ToList();
        if (list.Any(t => string.Equals(t, theme, StringComparison.CurrentCultureIgnoreCase))) return false;
        list.Add(theme);
        return Write(list);
    }

    public bool Remove(string theme)
    {
        var list = Read().ToList();
        var removed = list.RemoveAll(t => string.Equals(t, theme, StringComparison.CurrentCultureIgnoreCase)) > 0;
        return removed && Write(list);
    }

    /// <summary>Seeds <see cref="DefaultThemes"/> when the catalog file does not yet exist. No-op
    /// otherwise (so a user-emptied catalog is respected).</summary>
    public void SeedIfMissing()
    {
        if (File.Exists(_path)) return;
        Write(DefaultThemes);
    }

    private bool AtomicWriteText(string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
            return true;
        }
        catch { return false; }
    }
}
