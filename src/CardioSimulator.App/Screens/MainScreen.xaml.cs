using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Screens;

/// <summary>
/// The 3-section application shell (Top / mode screen / Bottom), faithful to the Android
/// <c>MainScreen</c>. The selected operating mode routes the middle section; per-mode
/// <see cref="MonitorViewModel"/> / <see cref="RhythmViewModel"/> are recreated on each
/// switch (the Android composables are keyed by mode). The Settings dialog content lands
/// in a later increment; the Editor mode screen lands with the editor milestone.
/// </summary>
public sealed partial class MainScreen : UserControl
{
    private AppViewModel? _appViewModel;
    private MonitorViewModel? _monitorViewModel;
    private RhythmViewModel? _rhythmViewModel;

    public MainScreen()
    {
        InitializeComponent();
    }

    public void Initialize(AppViewModel appViewModel)
    {
        _appViewModel = appViewModel;
        appViewModel.PropertyChanged += OnAppViewModelChanged;
        Bottom.SettingsClick += OnSettingsClick;
        BuildForMode();
    }

    private void OnAppViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedOperatingMode)) BuildForMode();
    }

    private async void BuildForMode()
    {
        if (_appViewModel is null) return;
        var appVm = _appViewModel;

        // Fresh per-mode view-models (Android keys them by mode id).
        _monitorViewModel = new MonitorViewModel();
        _rhythmViewModel = new RhythmViewModel(appVm.Repository);

        Top.Bind(appVm, _monitorViewModel, OnStartStop);

        var mode = appVm.SelectedOperatingMode.Id;
        UIElement screen;
        switch (mode)
        {
            case OperatingMode.Teaching:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var teaching = new TeachingScreen();
                teaching.Initialize(_monitorViewModel, _rhythmViewModel, appVm);
                screen = teaching;

                var teachingPanel = new MonitorControlPanel();
                teachingPanel.Bind(_monitorViewModel);
                teachingPanel.StartStopClick += (_, running) => OnStartStop(running);
                Bottom.PanelContent = teachingPanel;
                break;

            case OperatingMode.Testing:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var testing = new TestingScreen();
                testing.Initialize(_monitorViewModel, _rhythmViewModel);
                testing.StartStopClick += (_, running) => OnStartStop(running);
                screen = testing;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.Examination:
                screen = new ExaminationScreen();
                Bottom.PanelContent = null;
                break;

            case OperatingMode.OSKE:
                screen = new OSKEScreen();
                Bottom.PanelContent = null;
                break;

            case OperatingMode.Editor:
                // Real EditorScreen lands with the editor milestone.
                screen = PlaceholderScreen("Editor");
                Bottom.PanelContent = null;
                break;

            default:
                screen = PlaceholderScreen(mode.ToString());
                Bottom.PanelContent = null;
                break;
        }

        MiddleHost.Children.Clear();
        MiddleHost.Children.Add(screen);

        await _rhythmViewModel.LoadManifestAsync();
    }

    private void OnStartStop(bool isRunning)
    {
        if (_appViewModel is null || _rhythmViewModel is null) return;
        if (isRunning)
        {
            _appViewModel.SendStartCommand(_rhythmViewModel.SelectedRhythm?.Id, _rhythmViewModel.SelectedRhythm?.TitleEn);
        }
        else
        {
            _appViewModel.SendStopCommand();
        }
    }

    private static UIElement PlaceholderScreen(string label) => new Grid
    {
        Children =
        {
            new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.Gray),
            },
        },
    };

    private async void OnSettingsClick(object? sender, EventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new TextBlock { Text = "Settings dialog — coming in a later increment." },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
