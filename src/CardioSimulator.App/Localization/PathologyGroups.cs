namespace CardioSimulator.App.Localization;

/// <summary>
/// Catalog of the rhythm groups used by the "all rhythms" group filter. The keys match the
/// <c>group:</c> field stored per pathology in <c>manifest.txt</c>; localized display names come
/// from <see cref="AppStrings.PathologyGroupName"/>. <see cref="OrderedKeys"/> defines the canonical
/// display order; any pathology whose group is missing/unknown falls into the trailing "Other" bucket.
/// </summary>
public static class PathologyGroups
{
    /// <summary>Synthetic key for pathologies with no (or an unrecognized) group.</summary>
    public const string Other = "__other__";

    /// <summary>Group keys in the order they should appear in the grouped list.</summary>
    public static readonly IReadOnlyList<string> OrderedKeys = new[]
    {
        "sinus",
        "arrhythmia",
        "conduction",
        "hypertrophy",
        "ischemia",
        "infarction",
        "electrolyte",
        "syndromes",
        "pacemaker",
        "special",
        "pediatric",
        "newborn",
        "pregnant",
    };

    private static readonly HashSet<string> Known = new(OrderedKeys);

    public static bool IsKnown(string? key) => key is not null && Known.Contains(key);

    /// <summary>Localized header for a group key, falling back to the "Other" label.</summary>
    public static string DisplayName(string? key) =>
        IsKnown(key) ? AppStrings.PathologyGroupName(key!) : AppStrings.RhythmGroupOther;
}
