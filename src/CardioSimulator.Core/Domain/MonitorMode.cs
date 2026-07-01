using System;
using System.Collections.Generic;
using CardioSimulator.Core.Data;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Paper-grid color scheme. <see cref="LabelResourceKey"/> is a localization
/// key resolved by the UI layer.
/// </summary>
public enum GridScheme
{
    Yellow,
    BlueGray,
    Pink,
}

public static class GridSchemes
{
    public static string LabelResourceKey(this GridScheme scheme) => scheme switch
    {
        GridScheme.Yellow => "grid_scheme_yellow",
        GridScheme.BlueGray => "grid_scheme_blue_gray",
        GridScheme.Pink => "grid_scheme_pink",
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

public enum EcgFilterType
{
    None,
    Lowpass,
    Highpass,
    Bandpass
}

/// <summary>
/// Recording-artifact noise that can be overlaid on the monitor trace, chosen from the "Артефакты"
/// menu. A <c>[Flags]</c> set so several artifacts can be active at once; the noise for each active
/// bit is generated (via <c>BioSPPy.Net</c>) and summed onto the clean signal.
/// </summary>
[Flags]
public enum EcgArtifacts
{
    None = 0,
    Muscle = 1 << 0,   // EMG / muscle-tremor fuzz
    Mains = 1 << 1,    // 50 Hz power-line interference
    Baseline = 1 << 2, // low-frequency baseline wander
    Contact = 1 << 3,  // electrode-contact pops
    Motion = 1 << 4,   // motion / movement excursions
}

/// <summary>
/// Electrode-hookup state demonstrated from the "Электроды" window. <see cref="Ok"/> is the correct
/// connection; <see cref="Swapped"/> models the classic RA/LA limb-electrode reversal; and
/// <see cref="Displacement"/> models precordial-electrode misplacement. Applied to the live monitor
/// trace by <see cref="ElectrodeFault"/> before any recording artifacts/filtering.
/// </summary>
public enum ElectrodeState
{
    Ok,
    Swapped,
    Displacement,
}

/// <summary>Immutable monitor configuration; copied on each setter via <c>with</c>.</summary>
public sealed record MonitorModeModel(
    int Count = 1,
    GridScheme GridScheme = GridScheme.Yellow,
    SeriesScheme SeriesScheme = SeriesScheme.OneColumn,
    float Speed = 25f,
    float Scale = 1f,
    float DisplayScale = 0.4f,
    EcgCalibration? Calibration = null,
    bool IsRunning = false,
    bool BlankSheet = false,
    bool ShowImpulseLabels = false,
    bool ShowImpulseGraphOverlay = false,
    bool IsCompareMode = false,
    EcgFilterType FilterType = EcgFilterType.None,
    EcgArtifacts Artifacts = EcgArtifacts.None,
    ElectrodeState ElectrodeState = ElectrodeState.Ok,
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
