using System.Text.Json;
using CardioSimulator.App.Data;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Holds the monitor display configuration. Faithful port of the Android
/// <c>MonitorViewModel</c>: just the <see cref="MonitorModeModel"/> and its setters.
/// Which rhythm is shown and its waveforms live in <see cref="RhythmViewModel"/>.
/// Per-mode persistence: reads from <c>${mode}_monitor_*</c> keys, falling back
/// to the legacy global key so old prefs files continue to work.
/// </summary>
public partial class MonitorViewModel : ObservableObject
{
    private readonly DataSourcePrefs? _prefs;
    private readonly string? _modePrefix; // e.g. "teaching", "constructor"

    [ObservableProperty]
    private MonitorModeModel _monitorMode = new();

    [ObservableProperty]
    private IReadOnlyList<ComparisonPreset> _comparisonPresets = Array.Empty<ComparisonPreset>();

    /// <summary>
    /// Latest signal-quality (SQI) readout for the currently displayed trace, or <c>null</c> when
    /// it can't be computed (no/short signal, or compare mode). Computed by the monitor view from
    /// the processed (filtered) waveform and surfaced in the Filters dropdown.
    /// </summary>
    [ObservableProperty]
    private SignalQualityInfo? _signalQuality;

    /// <summary>
    /// Whether the student has made an explicit electrode-hookup choice in the "Электроды" window.
    /// Stays <c>false</c> for a fresh session (default <see cref="ElectrodeState.Ok"/> wiring), letting
    /// the control panel keep the Electrodes tab neutral until it's been used, then highlight it green
    /// (confirmed OK) or red (a fault is active). Not persisted.
    /// </summary>
    [ObservableProperty]
    private bool _electrodeStateUserSet;

    public MonitorViewModel(DataSourcePrefs? prefs = null, string? modePrefix = null)
    {
        _prefs = prefs;
        _modePrefix = modePrefix;
        if (_prefs is not null)
        {
            var mode = _monitorMode;
            if (ReadPref("grid_scheme") is { } schemeName && Enum.TryParse<GridScheme>(schemeName, out var scheme))
                mode = mode with { GridScheme = scheme };
            if (ReadPref("monitor_speed") is { } speedStr
                    && float.TryParse(speedStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var speed))
                mode = mode with { Speed = speed };
            if (ReadPref("monitor_scale") is { } scaleStr
                    && float.TryParse(scaleStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var scale))
                mode = mode with { Scale = scale };
            if (ReadPref("monitor_display_scale") is { } dsStr
                    && float.TryParse(dsStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ds))
                mode = mode with { DisplayScale = ds };
            if (ReadPref("monitor_series_count") is { } countStr && int.TryParse(countStr, out var count))
                mode = mode with { Count = count };
            if (ReadPref("monitor_series_scheme") is { } ssName && Enum.TryParse<SeriesScheme>(ssName, out var ss))
                mode = mode with { SeriesScheme = ss };
            if (ReadPref("blank_sheet") is { } bsStr && bool.TryParse(bsStr, out var bs))
                mode = mode with { BlankSheet = bs };
            if (ReadPref("monitor_filter_type") is { } ftName && Enum.TryParse<EcgFilterType>(ftName, out var ft))
                mode = mode with { FilterType = ft };
            if (ReadPref("monitor_artifacts") is { } artName && Enum.TryParse<EcgArtifacts>(artName, out var art))
                mode = mode with { Artifacts = art };
            _monitorMode = mode;

            ComparisonPresets = LoadPresets();
        }
    }

    // ── Per-mode prefs helpers ──────────────────────────────────────────────

    // Reads from the mode-scoped key first, falls back to the global key.
    private string? ReadPref(string shortKey)
    {
        if (_prefs is null) return null;
        if (_modePrefix is not null)
        {
            var val = _prefs.GetRaw($"{_modePrefix}_{shortKey}");
            if (val is not null) return val;
        }
        return _prefs.GetRaw(shortKey);
    }

    // Writes always go to the mode-scoped key when a prefix is set.
    private void WritePref(string shortKey, string? value)
    {
        if (_prefs is null) return;
        var key = _modePrefix is not null ? $"{_modePrefix}_{shortKey}" : shortKey;
        _prefs.SetRaw(key, value);
    }

    // ── Setters ────────────────────────────────────────────────────────────

    public void SetSeriesCount(int count)
    {
        // A manual count change reverts to the default canonical lead order.
        MonitorMode = MonitorMode with { Count = count, LeadSelection = null };
        WritePref("monitor_series_count", count.ToString());
    }

    /// <summary>
    /// Displays an explicit, ordered set of leads (e.g. an <c>&lt;ecg&gt;</c> embed's handpicked
    /// leads), syncing <see cref="MonitorModeModel.Count"/> to match. An empty list reverts to the
    /// canonical first-12. Cleared by <see cref="SetSeriesCount"/>.
    /// </summary>
    public void SetLeadSelection(IReadOnlyList<Lead> leads)
    {
        if (leads.Count == 0) { SetSeriesCount(12); return; }
        MonitorMode = MonitorMode with { Count = leads.Count, LeadSelection = leads };
        WritePref("monitor_series_count", leads.Count.ToString());
    }

    public void SetSeriesScheme(SeriesScheme scheme)
    {
        MonitorMode = MonitorMode with { SeriesScheme = scheme };
        WritePref("monitor_series_scheme", scheme.ToString());
    }

    public void SetGridScheme(GridScheme scheme)
    {
        MonitorMode = MonitorMode with { GridScheme = scheme };
        WritePref("grid_scheme", scheme.ToString());
    }

    public void SetBlankSheet(bool blankSheet)
    {
        MonitorMode = MonitorMode with { BlankSheet = blankSheet };
        WritePref("blank_sheet", blankSheet.ToString());
    }

    public void SetFilterType(EcgFilterType filterType)
    {
        MonitorMode = MonitorMode with { FilterType = filterType };
        WritePref("monitor_filter_type", filterType.ToString());
    }

    /// <summary>
    /// Sets the active recording-artifact set (a <see cref="EcgArtifacts"/> flags combination). The
    /// monitor regenerates and overlays the corresponding noise on the trace.
    /// </summary>
    public void SetArtifacts(EcgArtifacts artifacts)
    {
        MonitorMode = MonitorMode with { Artifacts = artifacts };
        WritePref("monitor_artifacts", artifacts.ToString());
    }

    /// <summary>
    /// Sets the electrode-hookup fault demonstrated from the "Электроды" window. The monitor
    /// remaps the live trace (RA/LA reversal, or attenuated precordial leads) via
    /// <see cref="ElectrodeFault"/>. Not persisted — a fresh session always starts correctly wired.
    /// </summary>
    public void SetElectrodeState(ElectrodeState state)
    {
        // Mark the choice before the model change so observers (the control panel) see the flag set
        // when they react to the MonitorMode notification.
        ElectrodeStateUserSet = true;
        MonitorMode = MonitorMode with { ElectrodeState = state };
    }

    public void SetSpeed(float speed)
    {
        MonitorMode = MonitorMode with { Speed = speed };
        WritePref("monitor_speed",
            speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetScale(float scale)
    {
        MonitorMode = MonitorMode with { Scale = scale };
        WritePref("monitor_scale",
            scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetCalibration(EcgCalibration calibration) => MonitorMode = MonitorMode with { Calibration = calibration };

    /// <summary>Pushes the latest computed signal-quality readout (or <c>null</c> to clear it).</summary>
    public void SetSignalQuality(SignalQualityInfo? info) => SignalQuality = info;

    public void SetDisplayScale(float displayScale)
    {
        MonitorMode = MonitorMode with { DisplayScale = displayScale };
        WritePref("monitor_display_scale",
            displayScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetIsRunning(bool isRunning) => MonitorMode = MonitorMode with { IsRunning = isRunning };

    /// <summary>
    /// Toggles the pQRSt readout: when on, the monitor shows the translucent measurements column
    /// (HR / R-R / P / PR / QRS / QT / ST / T) derived from the rhythm's significant-point markup.
    /// The on-trace annotations are gated separately by <see cref="SetShowImpulseGraphOverlay"/>.
    /// </summary>
    public void SetShowImpulseLabels(bool show) => MonitorMode = MonitorMode with { ShowImpulseLabels = show };

    /// <summary>
    /// Toggles the on-trace P/QRS/T markers and interval brackets (the "On graph" checkbox in the
    /// measurements column). Independent of <see cref="SetShowImpulseLabels"/>, though the renderer
    /// only draws them while the pQRSt readout is also on.
    /// </summary>
    public void SetShowImpulseGraphOverlay(bool show) => MonitorMode = MonitorMode with { ShowImpulseGraphOverlay = show };

    // ── Comparison mode ────────────────────────────────────────────────────

    /// <summary>
    /// Toggles compare mode. When turning it on with no existing targets (and no presets to
    /// offer), seeds two panes from <paramref name="defaultPathologyId"/> at leads I and II,
    /// mirroring the Android <c>toggleCompareMode</c>.
    /// </summary>
    public void ToggleCompareMode(string? defaultPathologyId = null)
    {
        var next = !MonitorMode.IsCompareMode;
        var targets = MonitorMode.ComparisonTargets;
        if (next && targets.Count == 0 && defaultPathologyId is not null)
        {
            // Default comparison seeds a single filled pane (the selected rhythm, Lead II); the
            // remaining panes start as tappable placeholders. Layout is sized by the caller.
            targets = new Dictionary<int, ComparisonTarget>
            {
                [0] = new ComparisonTarget(defaultPathologyId, Lead.II),
            };
        }
        MonitorMode = MonitorMode with { IsCompareMode = next, ComparisonTargets = targets };
    }

    public void ExitCompareMode() => MonitorMode = MonitorMode with { IsCompareMode = false };

    public void EnterCompareMode() => MonitorMode = MonitorMode with { IsCompareMode = true };

    public void SetComparisonTarget(int paneIndex, ComparisonTarget target)
    {
        var map = new Dictionary<int, ComparisonTarget>(MonitorMode.ComparisonTargets) { [paneIndex] = target };
        MonitorMode = MonitorMode with { ComparisonTargets = map };
    }

    public void RemoveComparisonTarget(int paneIndex)
    {
        if (!MonitorMode.ComparisonTargets.ContainsKey(paneIndex)) return;
        var map = new Dictionary<int, ComparisonTarget>(MonitorMode.ComparisonTargets);
        map.Remove(paneIndex);
        MonitorMode = MonitorMode with { ComparisonTargets = map };
    }

    public void ClearComparisonTargets() =>
        MonitorMode = MonitorMode with { ComparisonTargets = new Dictionary<int, ComparisonTarget>() };

    // ── Comparison presets ─────────────────────────────────────────────────

    /// <summary>Saves the current per-pane target layout under <paramref name="name"/>.</summary>
    public void SaveCurrentAsPreset(string name)
    {
        var targets = MonitorMode.ComparisonTargets;
        if (targets.Count == 0) return;
        var preset = new ComparisonPreset(name, new Dictionary<int, ComparisonTarget>(targets));
        var updated = ComparisonPresets.Where(p => p.Name != name).Append(preset).ToList();
        ComparisonPresets = updated;
        PersistPresets(updated);
    }

    /// <summary>Applies a saved layout and enters compare mode.</summary>
    public void ApplyPreset(ComparisonPreset preset)
    {
        MonitorMode = MonitorMode with
        {
            IsCompareMode = true,
            ComparisonTargets = new Dictionary<int, ComparisonTarget>(preset.Targets),
        };
    }

    public void DeletePreset(string name)
    {
        var updated = ComparisonPresets.Where(p => p.Name != name).ToList();
        ComparisonPresets = updated;
        PersistPresets(updated);
    }

    private IReadOnlyList<ComparisonPreset> LoadPresets()
    {
        var json = ReadPref("comparison_presets");
        if (json is null) return Array.Empty<ComparisonPreset>();
        try
        {
            var dtos = JsonSerializer.Deserialize<List<PresetDto>>(json);
            if (dtos is null) return Array.Empty<ComparisonPreset>();
            var result = new List<ComparisonPreset>();
            foreach (var dto in dtos)
            {
                if (dto.Name is null || dto.Targets is null) continue;
                var targets = new Dictionary<int, ComparisonTarget>();
                foreach (var (key, t) in dto.Targets)
                {
                    if (!int.TryParse(key, out var pane)) continue;
                    if (t.PathologyId is null || !Enum.TryParse<Lead>(t.Lead, out var lead)) continue;
                    targets[pane] = new ComparisonTarget(t.PathologyId, lead);
                }
                if (targets.Count > 0) result.Add(new ComparisonPreset(dto.Name, targets));
            }
            return result;
        }
        catch
        {
            return Array.Empty<ComparisonPreset>();
        }
    }

    private void PersistPresets(IReadOnlyList<ComparisonPreset> presets)
    {
        var dtos = presets.Select(p => new PresetDto
        {
            Name = p.Name,
            Targets = p.Targets.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new TargetDto { PathologyId = kv.Value.PathologyId, Lead = kv.Value.Lead.ToString() }),
        }).ToList();
        WritePref("comparison_presets", JsonSerializer.Serialize(dtos));
    }

    // Serialisation DTOs — kept internal to this file.
    private sealed class PresetDto
    {
        public string? Name { get; set; }
        public Dictionary<string, TargetDto>? Targets { get; set; }
    }

    private sealed class TargetDto
    {
        public string? PathologyId { get; set; }
        public string? Lead { get; set; }
    }
}

/// <summary>
/// A signal-quality (SQI) readout for one displayed trace. <see cref="Quality"/> is the ZZ2018
/// fuzzy label ("Excellent" / "Barely acceptable" / …); the three numeric indices back it up.
/// </summary>
public sealed record SignalQualityInfo(
    string Quality, double SSqi, double KSqi, double PSqi, Lead PrimaryLead);
