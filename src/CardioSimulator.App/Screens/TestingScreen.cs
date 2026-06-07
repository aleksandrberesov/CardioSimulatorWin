using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Testing mode: monitor + monitor-control panel on the left, rhythm chooser drawer on
/// the right. The drawer mirrors the Teaching mode pattern — collapsed by default, slides
/// open on tap. Port of the Android <c>TestingScreen</c>.
/// </summary>
public sealed class TestingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly MonitorControlPanel _controlPanel = new();
    private readonly RhythmChoosingDrawer _rhythmDrawer = new();
    private RhythmViewModel? _rhythmVm;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    public TestingScreen()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: monitor stacked above its control panel.
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_monitor, 0);
        left.Children.Add(_monitor);
        Grid.SetRow(_controlPanel, 1);
        left.Children.Add(_controlPanel);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right: collapsible rhythm chooser (handle always visible).
        _rhythmDrawer.HorizontalAlignment = HorizontalAlignment.Right;
        _rhythmDrawer.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_rhythmDrawer, 1);
        grid.Children.Add(_rhythmDrawer);

        Content = grid;

        _controlPanel.StartStopClick += (_, running) => StartStopClick?.Invoke(this, running);
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, CardioSimulator.Core.Domain.Language displayLanguage)
    {
        _rhythmVm = rhythmVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _controlPanel.Bind(monitorVm);

        _rhythmDrawer.DisplayLanguage = displayLanguage;
        _rhythmDrawer.SetRhythms(rhythmVm.Rhythms);
        _rhythmDrawer.SelectedId = rhythmVm.SelectedRhythm?.Id;
        _rhythmDrawer.RhythmSelected += (_, entry) => rhythmVm.SelectRhythm(entry.Id);

        rhythmVm.PropertyChanged += OnRhythmChanged;
        Unloaded += (_, _) => rhythmVm.PropertyChanged -= OnRhythmChanged;
    }

    // Backwards-compat overload for callers that don't pass a display language.
    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm)
        => Initialize(monitorVm, rhythmVm, CardioSimulator.Core.Domain.Language.EN);

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
            _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
        else if (e.PropertyName == nameof(RhythmViewModel.SelectedRhythm))
            _rhythmDrawer.SelectedId = _rhythmVm.SelectedRhythm?.Id;
    }
}
