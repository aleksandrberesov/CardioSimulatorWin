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
        MonitorMode = MonitorMode with { Count = count };
        WritePref("monitor_series_count", count.ToString());
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

    public void SetDisplayScale(float displayScale)
    {
        MonitorMode = MonitorMode with { DisplayScale = displayScale };
        WritePref("monitor_display_scale",
            displayScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetIsRunning(bool isRunning) => MonitorMode = MonitorMode with { IsRunning = isRunning };

    // ── Comparison mode ────────────────────────────────────────────────────

    public void ToggleCompareMode() => MonitorMode = MonitorMode with { IsCompareMode = !MonitorMode.IsCompareMode };

    public void ExitCompareMode() => MonitorMode = MonitorMode with { IsCompareMode = false };

    public void EnterCompareMode() => MonitorMode = MonitorMode with { IsCompareMode = true };

    // ── Comparison presets ─────────────────────────────────────────────────

    public void SaveCurrentAsPreset(string name, IReadOnlyList<string> pathologyIds, Lead lead)
    {
        var preset = new ComparisonPreset(name, pathologyIds, lead);
        var updated = ComparisonPresets.Where(p => p.Name != name).Append(preset).ToList();
        ComparisonPresets = updated;
        PersistPresets(updated);
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
            return dtos
                .Where(d => d.Name is not null && d.Ids is not null
                            && Enum.TryParse<Lead>(d.Lead, out _))
                .Select(d => new ComparisonPreset(d.Name!, d.Ids!, Enum.Parse<Lead>(d.Lead!)))
                .ToList();
        }
        catch
        {
            return Array.Empty<ComparisonPreset>();
        }
    }

    private void PersistPresets(IReadOnlyList<ComparisonPreset> presets)
    {
        var dtos = presets.Select(p => new PresetDto { Name = p.Name, Ids = p.PathologyIds.ToList(), Lead = p.Lead.ToString() }).ToList();
        WritePref("comparison_presets", JsonSerializer.Serialize(dtos));
    }

    // Serialisation DTO — kept internal to this file.
    private sealed class PresetDto
    {
        public string? Name { get; set; }
        public List<string>? Ids { get; set; }
        public string? Lead { get; set; }
    }
}
