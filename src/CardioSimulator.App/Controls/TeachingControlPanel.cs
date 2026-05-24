using System.ComponentModel;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Teaching-mode top sub-panel: an education-program dropdown plus a start/stop button.
/// Port of the Android <c>TeachingControlPanel</c>.
/// </summary>
public sealed class TeachingControlPanel : UserControl
{
    private static readonly string GlyphPlay = char.ConvertFromUtf32(0xE768);
    private static readonly string GlyphStop = char.ConvertFromUtf32(0xE71A);

    private static readonly string[] Programs =
    {
        "Program 1", "Program 2", "Program 3", "Program 4", "Program 5", "Program 6",
    };

    private readonly Tab _programTab = new();
    private readonly Tab _startStopTab = new();
    private MonitorViewModel? _viewModel;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    public TeachingControlPanel()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _programTab.Text = Programs[0];
        _programTab.Margin = new Thickness(4, 0, 4, 0);
        _programTab.Click += OnProgramClick;
        row.Children.Add(_programTab);

        _startStopTab.Glyph = GlyphPlay;
        _startStopTab.Margin = new Thickness(4, 0, 4, 0);
        _startStopTab.Click += OnStartStopClick;
        row.Children.Add(_startStopTab);

        Content = row;
    }

    public void Bind(MonitorViewModel viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnVmChanged;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnVmChanged;
        UpdateGlyph();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode)) UpdateGlyph();
    }

    private void UpdateGlyph()
    {
        if (_viewModel is null) return;
        _startStopTab.Glyph = _viewModel.MonitorMode.IsRunning ? GlyphStop : GlyphPlay;
    }

    private void OnProgramClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var program in Programs)
        {
            var captured = program;
            var item = new MenuFlyoutItem { Text = captured };
            item.Click += (_, _) => _programTab.Text = captured;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_programTab);
    }

    private void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var newState = !_viewModel.MonitorMode.IsRunning;
        _viewModel.SetIsRunning(newState);
        StartStopClick?.Invoke(this, newState);
    }
}
