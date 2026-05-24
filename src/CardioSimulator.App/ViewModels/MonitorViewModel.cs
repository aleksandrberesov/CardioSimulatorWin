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
    [ObservableProperty]
    private MonitorModeModel _monitorMode = new();

    public void SetSeriesCount(int count) => MonitorMode = MonitorMode with { Count = count };

    public void SetSeriesScheme(SeriesScheme scheme) => MonitorMode = MonitorMode with { SeriesScheme = scheme };

    public void SetGridScheme(GridScheme scheme) => MonitorMode = MonitorMode with { GridScheme = scheme };

    public void SetSpeed(int speed) => MonitorMode = MonitorMode with { Speed = speed };

    public void SetScale(float scale) => MonitorMode = MonitorMode with { Scale = scale };

    public void SetCalibration(EcgCalibration calibration) => MonitorMode = MonitorMode with { Calibration = calibration };

    public void SetDisplayScale(float displayScale) => MonitorMode = MonitorMode with { DisplayScale = displayScale };

    public void SetIsRunning(bool isRunning) => MonitorMode = MonitorMode with { IsRunning = isRunning };
}
