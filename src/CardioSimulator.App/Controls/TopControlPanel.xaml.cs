using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Top bar: the operating-mode selector (Tab + dropdown), the per-mode control
/// sub-panel (TeachingControlPanel / TestingControlPanel), and the app logo. Faithful
/// port of the Android <c>TopControlPanel</c>.
/// </summary>
public sealed partial class TopControlPanel : UserControl
{
    private AppViewModel? _viewModel;
    private RhythmViewModel? _rhythmViewModel;

    public TopControlPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Binds the panel for the current operating mode. Called by <c>MainScreen</c> on
    /// every mode switch with the mode's fresh <see cref="RhythmViewModel"/>.
    /// </summary>
    public void Bind(AppViewModel appViewModel, RhythmViewModel rhythmViewModel)
    {
        _viewModel = appViewModel;
        _rhythmViewModel = rhythmViewModel;
        UpdateMode();
    }

    private void UpdateMode()
    {
        if (_viewModel is null) return;
        ModeTab.Text = AppStrings.ModeName(_viewModel.SelectedOperatingMode.Id);
        SubPanelHost.Content = BuildSubPanel(_viewModel.SelectedOperatingMode.Id);
    }

    private object? BuildSubPanel(OperatingMode mode)
    {
        switch (mode)
        {
            case OperatingMode.Teaching when _viewModel is not null && _rhythmViewModel is not null:
                var teaching = new TeachingControlPanel();
                teaching.Bind(_viewModel, _rhythmViewModel);
                return teaching;
            // Testing's counter/timer live in the question panel on the right of the screen, so the
            // top bar needs no Testing sub-panel.
            default:
                return null;
        }
    }

    private void OnModeClick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var flyout = new MenuFlyout();
        foreach (var mode in _viewModel.OperatingModes)
        {
            var captured = mode;
            var item = new MenuFlyoutItem { Text = AppStrings.ModeName(mode.Id) };
            item.Click += (_, _) => _viewModel.UpdateOperatingMode(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ModeTab);
    }
}
