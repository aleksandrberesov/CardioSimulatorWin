using System.ComponentModel;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Hosts the Win2D <see cref="EcgMonitorControl"/> and keeps it bound to the shared
/// <see cref="MonitorViewModel"/> (display mode) and <see cref="RhythmViewModel"/>
/// (waveforms). Mirrors the Android <c>Monitor</c> display wrapper; zoom/pan is added
/// to the hosted control in a later increment.
/// </summary>
public sealed class MonitorView : Grid
{
    private readonly EcgMonitorControl _monitor = new();
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;

    public MonitorView()
    {
        Children.Add(_monitor);
    }

    public EcgMonitorControl Monitor => _monitor;

    public void Bind(MonitorViewModel monitorVm, RhythmViewModel rhythmVm)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _monitor.Mode = monitorVm.MonitorMode;
        _monitor.Waveforms = rhythmVm.Waveforms;
        monitorVm.PropertyChanged += OnMonitorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode) && _monitorVm is not null)
        {
            _monitor.Mode = _monitorVm.MonitorMode;
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RhythmViewModel.Waveforms) && _rhythmVm is not null)
        {
            _monitor.Waveforms = _rhythmVm.Waveforms;
        }
    }
}
