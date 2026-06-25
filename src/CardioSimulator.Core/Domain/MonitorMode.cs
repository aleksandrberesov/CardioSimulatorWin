using System.Collections.Generic;
using CardioSimulator.Core.Data;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Paper-grid color scheme. <see cref="LabelResourceKey"/> is a localization
/// key resolved by the UI layer.
/// </summary>
public enum GridScheme
{
    Pink,
    BlueGray,
}

public static class GridSchemes
{
    public static string LabelResourceKey(this GridScheme scheme) => scheme switch
    {
        GridScheme.Pink => "grid_scheme_pink",
        GridScheme.BlueGray => "grid_scheme_blue_gray",
        _ => scheme.ToString(),
    };
}

/// <summary>How the lead cells are arranged on the monitor.</summary>
public enum SeriesScheme
{
    OneColumn,
    TwoColumn,
    Grid,
}

public static class SeriesSchemes
{
    /// <summary>Parses a scheme token (<c>onecolumn</c> / <c>twocolumn</c> / <c>grid</c>),
    /// defaulting to <see cref="SeriesScheme.OneColumn"/>.</summary>
    public static SeriesScheme Parse(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "twocolumn" or "two" or "2" => SeriesScheme.TwoColumn,
        "grid" => SeriesScheme.Grid,
        _ => SeriesScheme.OneColumn,
    };

    /// <summary>The token written to the <c>&lt;ecg scheme="…"&gt;</c> attribute.</summary>
    public static string ToToken(this SeriesScheme scheme) => scheme switch
    {
        SeriesScheme.TwoColumn => "twocolumn",
        SeriesScheme.Grid => "grid",
        _ => "onecolumn",
    };

    /// <summary>Maximum number of columns the scheme lays cells into (1 / 2 / 4),
    /// shared by the live monitor and the static lecture figure.</summary>
    public static int MaxColumns(this SeriesScheme scheme) => scheme switch
    {
        SeriesScheme.OneColumn => 1,
        SeriesScheme.TwoColumn => 2,
        SeriesScheme.Grid => 4,
        _ => 1,
    };
}

/// <summary>Immutable monitor configuration; copied on each setter via <c>with</c>.</summary>
public sealed record MonitorModeModel(
    int Count = 1,
    GridScheme GridScheme = GridScheme.Pink,
    SeriesScheme SeriesScheme = SeriesScheme.OneColumn,
    float Speed = 25f,
    float Scale = 1f,
    float DisplayScale = 0.4f,
    EcgCalibration? Calibration = null,
    bool IsRunning = false,
    bool BlankSheet = false,
    bool ShowImpulseLabels = false,
    bool IsCompareMode = false,
    IReadOnlyDictionary<int, ComparisonTarget>? ComparisonTargets = null,
    IReadOnlyList<Lead>? LeadSelection = null)
{
    /// <summary>Calibration, defaulting to standard constants when unset.</summary>
    public EcgCalibration Calibration { get; init; } = Calibration ?? new EcgCalibration();

    /// <summary>Per-pane comparison targets, keyed by pane index. Empty outside compare mode.</summary>
    public IReadOnlyDictionary<int, ComparisonTarget> ComparisonTargets { get; init; }
        = ComparisonTargets ?? new Dictionary<int, ComparisonTarget>();

    /// <summary>Explicit leads to display, in order (e.g. an <c>&lt;ecg&gt;</c> embed's handpicked
    /// leads). Null/empty falls back to the first <see cref="Count"/> leads in canonical order.</summary>
    public IReadOnlyList<Lead>? LeadSelection { get; init; } = LeadSelection;
}
