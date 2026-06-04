using CardioSimulator.App.Data;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Holds the monitor display configuration. Faithful port of the Android
/// <c>MonitorViewModel</c>: just the <see cref="MonitorModeModel"/> and its setters.
/// Which rhythm is shown and its waveforms live in <see cref="RhythmViewModel"/>.
/// </summary>
public partial class MonitorViewModel : ObservableObject
{
    private readonly DataSourcePrefs? _prefs;

    [ObservableProperty]
    private MonitorModeModel _monitorMode = new();

    public MonitorViewModel(DataSourcePrefs? prefs = null)
    {
        _prefs = prefs;
        if (_prefs is not null)
        {
            var mode = _monitorMode;
            if (_prefs.GridScheme is { } schemeName && Enum.TryParse<GridScheme>(schemeName, out var scheme))
                mode = mode with { GridScheme = scheme };
            if (_prefs.MonitorSpeed is { } speed)
                mode = mode with { Speed = (float)speed };
            if (_prefs.MonitorScale is { } scale)
                mode = mode with { Scale = scale };
            if (_prefs.MonitorDisplayScale is { } displayScale)
                mode = mode with { DisplayScale = displayScale };
            if (_prefs.MonitorSeriesCount is { } count)
                mode = mode with { Count = count };
            if (_prefs.MonitorSeriesScheme is { } seriesSchemeName && Enum.TryParse<SeriesScheme>(seriesSchemeName, out var seriesScheme))
                mode = mode with { SeriesScheme = seriesScheme };
            
            _monitorMode = mode;
        }
    }

    public void SetSeriesCount(int count)
    {
        MonitorMode = MonitorMode with { Count = count };
        if (_prefs is not null) _prefs.MonitorSeriesCount = count;
    }

    public void SetSeriesScheme(SeriesScheme scheme)
    {
        MonitorMode = MonitorMode with { SeriesScheme = scheme };
        if (_prefs is not null) _prefs.MonitorSeriesScheme = scheme.ToString();
    }

    public void SetGridScheme(GridScheme scheme)
    {
        MonitorMode = MonitorMode with { GridScheme = scheme };
        if (_prefs is not null) _prefs.GridScheme = scheme.ToString();
    }

    public void SetBlankSheet(bool blankSheet)
    {
        MonitorMode = MonitorMode with { BlankSheet = blankSheet };
        if (_prefs is not null) _prefs.BlankSheet = blankSheet;
    }

    public void SetSpeed(float speed)
    {
        MonitorMode = MonitorMode with { Speed = speed };
        if (_prefs is not null) _prefs.MonitorSpeed = speed;
    }

    public void SetScale(float scale)
    {
        MonitorMode = MonitorMode with { Scale = scale };
        if (_prefs is not null) _prefs.MonitorScale = scale;
    }

    public void SetCalibration(EcgCalibration calibration) => MonitorMode = MonitorMode with { Calibration = calibration };

    public void SetDisplayScale(float displayScale)
    {
        MonitorMode = MonitorMode with { DisplayScale = displayScale };
        if (_prefs is not null) _prefs.MonitorDisplayScale = displayScale;
    }

    public void SetIsRunning(bool isRunning) => MonitorMode = MonitorMode with { IsRunning = isRunning };
}
