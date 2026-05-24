using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Teaching mode: the monitor with a collapsible rhythm drawer on the left.
/// Port of the Android <c>TeachingScreen</c>.
/// </summary>
public sealed class TeachingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly RhythmChoosingDrawer _drawer = new();
    private RhythmViewModel? _rhythmVm;

    public TeachingScreen()
    {
        var grid = new Grid();
        _monitor.Margin = new Thickness(24, 0, 0, 0);
        grid.Children.Add(_monitor);

        _drawer.HorizontalAlignment = HorizontalAlignment.Left;
        _drawer.VerticalAlignment = VerticalAlignment.Stretch;
        grid.Children.Add(_drawer);

        Content = grid;
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _rhythmVm = rhythmVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _drawer.DisplayLanguage = appVm.SelectedLanguage;
        _drawer.SetRhythms(rhythmVm.Rhythms);
        _drawer.SelectedId = rhythmVm.SelectedRhythm?.Id;
        _drawer.RhythmSelected += (_, entry) => rhythmVm.SelectRhythm(entry.Id);
        rhythmVm.PropertyChanged += OnRhythmChanged;
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms) && _rhythmVm is not null)
        {
            _drawer.SetRhythms(_rhythmVm.Rhythms);
        }
    }
}
