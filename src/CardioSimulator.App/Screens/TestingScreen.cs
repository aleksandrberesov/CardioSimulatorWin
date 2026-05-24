using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Testing mode: monitor + monitor-control panel in the left column, empty right column.
/// Port of the Android <c>TestingScreen</c>.
/// </summary>
public sealed class TestingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly MonitorControlPanel _controlPanel = new();

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    public TestingScreen()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_monitor, 0);
        left.Children.Add(_monitor);
        Grid.SetRow(_controlPanel, 1);
        left.Children.Add(_controlPanel);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        Content = grid;

        _controlPanel.StartStopClick += (_, running) => StartStopClick?.Invoke(this, running);
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm)
    {
        _monitor.Bind(monitorVm, rhythmVm);
        _controlPanel.Bind(monitorVm);
    }
}
