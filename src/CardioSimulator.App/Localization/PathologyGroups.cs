using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Localization;

/// <summary>
/// Catalog of the rhythm groups used by the "all rhythms" group filter. The keys match the
/// <c>group:</c> field stored per pathology; the ordered list and localized names are loaded from
/// the dataset's bundled <c>groups.txt</c> (see <see cref="Load"/>). When that file is absent
/// (legacy datasets) it falls back to a built-in key list with names from <see cref="AppStrings"/>.
/// Any pathology whose group is missing/unknown falls into the trailing "Other" bucket.
/// </summary>
public static class PathologyGroups
{
    /// <summary>Synthetic key for pathologies with no (or an unrecognized) group.</summary>
    public const string Other = "__other__";

    /// <summary>Built-in fallback order, used only when the dataset ships no <c>groups.txt</c>.</summary>
    private static readonly IReadOnlyList<string> FallbackKeys = new[]
    {
        "sinus", "arrhythmia", "conduction", "hypertrophy", "ischemia", "infarction",
        "electrolyte", "syndromes", "pacemaker", "special", "pediatric", "newborn", "pregnant",
        "clinical",
    };

    private sealed record GroupDef(string Key, IReadOnlyDictionary<string, string> Names);

    private static readonly string[] LangTags = { "ru", "en", "zh", "es" };

    private static IReadOnlyList<GroupDef> _catalog = Array.Empty<GroupDef>();
    private static IReadOnlyList<string> _orderedKeys = FallbackKeys;
    private static HashSet<string> _known = new(FallbackKeys);
    private static string? _datasetDir;

    /// <summary>
    /// Loads the group catalog from <c>groups.txt</c> in <paramref name="datasetDir"/>. Safe to call
    /// repeatedly (e.g. whenever the manifest reloads). Falls back to the built-in keys if the file
    /// is missing, unreadable, or empty.
    /// </summary>
    public static void Load(string datasetDir)
    {
        _datasetDir = datasetDir;
        IReadOnlyList<GroupDef> parsed = Array.Empty<GroupDef>();
        try
        {
            var path = Path.Combine(datasetDir, "groups.txt");
            if (File.Exists(path)) parsed = Parse(File.ReadAllText(path));
        }
        catch
        {
            parsed = Array.Empty<GroupDef>();
        }

        _catalog = parsed;
        _orderedKeys = parsed.Count > 0 ? parsed.Select(g => g.Key).ToList() : FallbackKeys;
        _known = new HashSet<string>(_orderedKeys);
    }

    private static IReadOnlyList<GroupDef> Parse(string text)
    {
        var list = new List<GroupDef>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (!line.StartsWith("group:")) continue;

            string? key = null;
            var names = new Dictionary<string, string>();
            foreach (var field in line.Split(';'))
            {
                var i = field.IndexOf(':');
                if (i <= 0) continue;
                var k = field.Substring(0, i).Trim();
                var v = field.Substring(i + 1).Trim();
                if (k == "group") key = v;
                else if (v.Length > 0) names[k] = v; // language tag → name
            }
            if (!string.IsNullOrEmpty(key)) list.Add(new GroupDef(key, names));
        }
        return list;
    }

    /// <summary>Group keys in the order they should appear in the grouped list.</summary>
    public static IReadOnlyList<string> OrderedKeys => _orderedKeys;

    public static bool IsKnown(string? key) => key is not null && _known.Contains(key);

    /// <summary>Localized header for a group key (current UI language), falling back to the built-in
    /// name, then the "Other" label.</summary>
    public static string DisplayName(string? key)
    {
        if (!IsKnown(key)) return AppStrings.RhythmGroupOther;

        var def = _catalog.FirstOrDefault(g => g.Key == key);
        if (def is not null)
        {
            var tag = AppStrings.Current.Tag();
            if (def.Names.TryGetValue(tag, out var name) && name.Length > 0) return name;
            if (def.Names.TryGetValue("en", out var en) && en.Length > 0) return en;
        }
        return AppStrings.PathologyGroupName(key!);
    }

    /// <summary>True when a new group can be persisted (a dataset dir is known).</summary>
    public static bool CanCreate => _datasetDir is not null;

    /// <summary>
    /// Creates a new group from a display name: derives a unique key, appends it to the catalog and
    /// the dataset's <c>groups.txt</c>, reloads, and returns the new key (null on failure). The
    /// entered name is stored for every UI language (edit <c>groups.txt</c> later for translations).
    /// </summary>
    public static string? CreateGroup(string displayName)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || _datasetDir is null) return null;

        var key = MakeUniqueKey(name);
        var names = LangTags.ToDictionary(t => t, _ => name);
        var updated = Materialized().Append(new GroupDef(key, names)).ToList();
        if (!WriteGroupsFile(_datasetDir, updated)) return null;

        Load(_datasetDir); // re-read so state matches the file exactly
        return key;
    }

    /// <summary>Current catalog, or the built-in fallback materialized into defs (rare: no groups.txt).</summary>
    private static IReadOnlyList<GroupDef> Materialized()
    {
        if (_catalog.Count > 0) return _catalog;
        return FallbackKeys
            .Select(k => new GroupDef(k, LangTags.ToDictionary(t => t, _ => AppStrings.PathologyGroupName(k))))
            .ToList();
    }

    private static string MakeUniqueKey(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var baseKey = new string(chars).Trim('_');
        if (string.IsNullOrEmpty(baseKey)) baseKey = "group";
        var key = baseKey;
        var n = 1;
        while (_known.Contains(key) || key == Other) key = baseKey + ++n;
        return key;
    }

    private static bool WriteGroupsFile(string datasetDir, IReadOnlyList<GroupDef> groups)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("version:1.0\n");
            sb.Append("groups:").Append(groups.Count).Append('\n').Append('\n');
            foreach (var g in groups)
            {
                sb.Append("group:").Append(g.Key);
                foreach (var tag in LangTags)
                    if (g.Names.TryGetValue(tag, out var n) && n.Length > 0)
                        sb.Append(';').Append(tag).Append(':').Append(n);
                sb.Append('\n');
            }
            File.WriteAllText(Path.Combine(datasetDir, "groups.txt"), sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // no BOM
            return true;
        }
        catch
        {
            return false;
        }
    }
}
