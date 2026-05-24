using System.ComponentModel;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The monitor's bottom control row (Teaching mode). Faithful port of the Android
/// <c>MonitorControlPanel</c>: count / scheme / speed / scale dropdowns plus the
/// electrode / EOS / HR / Tips / ruler / start-stop controls. Drives a
/// <see cref="MonitorViewModel"/>.
/// </summary>
public sealed partial class MonitorControlPanel : UserControl
{
    private const string GlyphPlay = "";
    private const string GlyphStop = "";

    private MonitorViewModel? _viewModel;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    public MonitorControlPanel()
    {
        InitializeComponent();
    }

    public void Bind(MonitorViewModel viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        UpdateTexts();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode)) UpdateTexts();
    }

    private void UpdateTexts()
    {
        if (_viewModel is null) return;
        var mode = _viewModel.MonitorMode;
        CountTab.Text = $"{mode.Count}×";
        SchemeTab.Text = mode.SeriesScheme switch
        {
            SeriesScheme.OneColumn => "1 Col",
            SeriesScheme.TwoColumn => "2 Cols",
            SeriesScheme.Grid => "Grid",
            _ => string.Empty,
        };
        SpeedTab.Text = mode.Speed.ToString();
        ScaleTab.Text = $"{(int)(mode.Scale * 100)}%";
        StartStopTab.Glyph = mode.IsRunning ? GlyphStop : GlyphPlay;
    }

    private void OnCountClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var count in new[] { 1, 6, 12 })
        {
            var captured = count;
            var item = new MenuFlyoutItem { Text = $"{captured}×" };
            item.Click += (_, _) => _viewModel?.SetSeriesCount(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(CountTab);
    }

    private void OnSchemeClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        AddSchemeItem(flyout, "1 Column", SeriesScheme.OneColumn);
        AddSchemeItem(flyout, "2 Columns", SeriesScheme.TwoColumn);
        AddSchemeItem(flyout, "Grid", SeriesScheme.Grid);
        flyout.ShowAt(SchemeTab);
    }

    private void AddSchemeItem(MenuFlyout flyout, string text, SeriesScheme scheme)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => _viewModel?.SetSeriesScheme(scheme);
        flyout.Items.Add(item);
    }

    private void OnSpeedClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var speed in new[] { 25, 50 })
        {
            var captured = speed;
            var item = new MenuFlyoutItem { Text = $"{captured} mm/s" };
            item.Click += (_, _) => _viewModel?.SetSpeed(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(SpeedTab);
    }

    private void OnScaleClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var scale in new[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f })
        {
            var captured = scale;
            var item = new MenuFlyoutItem { Text = $"{(int)(captured * 100)}%" };
            item.Click += (_, _) => _viewModel?.SetScale(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ScaleTab);
    }

    private void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var newState = !_viewModel.MonitorMode.IsRunning;
        _viewModel.SetIsRunning(newState);
        StartStopClick?.Invoke(this, newState);
    }
}
