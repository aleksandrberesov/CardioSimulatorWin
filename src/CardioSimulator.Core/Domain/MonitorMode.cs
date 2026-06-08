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
    bool IsCompareMode = false,
    IReadOnlyDictionary<int, ComparisonTarget>? ComparisonTargets = null)
{
    /// <summary>Calibration, defaulting to standard constants when unset.</summary>
    public EcgCalibration Calibration { get; init; } = Calibration ?? new EcgCalibration();

    /// <summary>Per-pane comparison targets, keyed by pane index. Empty outside compare mode.</summary>
    public IReadOnlyDictionary<int, ComparisonTarget> ComparisonTargets { get; init; }
        = ComparisonTargets ?? new Dictionary<int, ComparisonTarget>();
}
