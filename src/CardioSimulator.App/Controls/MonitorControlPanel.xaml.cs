using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The monitor's bottom control row (Teaching mode). Faithful port of the Android
/// <c>MonitorControlPanel</c>: count / scheme / speed / scale dropdowns plus the
/// electrode / EOS / HR / Tips / ruler / start-stop controls. Drives a
/// <see cref="MonitorViewModel"/>. All labels are routed through <see cref="AppStrings"/>.
/// </summary>
public sealed partial class MonitorControlPanel : UserControl
{
    private static readonly string GlyphPlay = char.ConvertFromUtf32(0xE768);
    private static readonly string GlyphStop = char.ConvertFromUtf32(0xE71A);

    private MonitorViewModel? _viewModel;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    /// <summary>Raised when the Compare button is clicked (Teaching mode multi-rhythm overlay).</summary>
    public event EventHandler? CompareClick;

    public MonitorControlPanel()
    {
        InitializeComponent();
        SetStaticLabels();
    }

    private void SetStaticLabels()
    {
        ElectrodesTab.Text = AppStrings.MonitorElectrodes;
        EmdTab.Text = AppStrings.MonitorEmdEbpa;
        MuscleTab.Text = AppStrings.MonitorMuscle;
        EosText.Text = AppStrings.MonitorEos;
        HrText.Text = AppStrings.MonitorHrFormat(160);
        TipsTab.Text = AppStrings.MonitorTips;
        SpeedTab.SubText = AppStrings.MonitorSpeedUnit;
        CompareTab.Text = AppStrings.CompareButton;
    }

    private void OnCompareClick(object? sender, EventArgs e) => CompareClick?.Invoke(this, EventArgs.Empty);

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
        CountTab.Text = AppStrings.MonitorCountFormat(mode.Count);
        SchemeTab.Text = mode.SeriesScheme switch
        {
            SeriesScheme.OneColumn => AppStrings.MonitorColumnsOneShort,
            SeriesScheme.TwoColumn => AppStrings.MonitorColumnsTwoShort,
            SeriesScheme.Grid => AppStrings.MonitorColumnsGridShort,
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
            var item = new MenuFlyoutItem { Text = AppStrings.MonitorCountFormat(captured) };
            item.Click += (_, _) => _viewModel?.SetSeriesCount(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(CountTab);
    }

    private void OnSchemeClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        AddSchemeItem(flyout, AppStrings.MonitorColumnsOne, SeriesScheme.OneColumn);
        AddSchemeItem(flyout, AppStrings.MonitorColumnsTwo, SeriesScheme.TwoColumn);
        AddSchemeItem(flyout, AppStrings.MonitorColumnsGrid, SeriesScheme.Grid);
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
            var item = new MenuFlyoutItem { Text = AppStrings.MonitorSpeedFormat(captured) };
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
