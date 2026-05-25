using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

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
    private Func<Task<StorageFile?>>? _pickOpenZip;
    private Func<Task<StorageFile?>>? _pickSaveZip;

    public MainScreen()
    {
        InitializeComponent();
    }

    public void Initialize(
        AppViewModel appViewModel,
        Func<Task<StorageFile?>> pickOpenZip,
        Func<Task<StorageFile?>> pickSaveZip)
    {
        _appViewModel = appViewModel;
        _pickOpenZip = pickOpenZip;
        _pickSaveZip = pickSaveZip;
        appViewModel.PropertyChanged += OnAppViewModelChanged;
        AppStrings.Changed += OnLanguageChanged;
        Bottom.SettingsClick += OnSettingsClick;
        BuildForMode();
    }

    private void OnLanguageChanged() => BuildForMode();

    private void OnAppViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedOperatingMode)) BuildForMode();     
    }

    private async void BuildForMode()
    {
        if (_appViewModel is null) return;
        var appVm = _appViewModel;

        // Fresh per-mode view-models (Android keys them by mode id).
        _monitorViewModel = new MonitorViewModel(appVm.Prefs);
        _rhythmViewModel = new RhythmViewModel(appVm.Repository, appVm.Prefs);

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
                var editorViewModel = new EditorViewModel(appVm.Repository);
                var editor = new EditorScreen();
                editor.Initialize(editorViewModel, _monitorViewModel, _rhythmViewModel, appVm);
                screen = editor;
                Bottom.PanelContent = new EditorControlPanel(editorViewModel, _monitorViewModel);
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
        if (_appViewModel is null || _monitorViewModel is null ||
            _pickOpenZip is null || _pickSaveZip is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = AppStrings.SettingsTitle,
            CloseButtonText = AppStrings.SettingsClose,
            XamlRoot = XamlRoot,
        };
        dialog.Content = new SettingsContent(
            _appViewModel, _monitorViewModel, _pickOpenZip, _pickSaveZip, () => dialog.Hide());
        await dialog.ShowAsync();
    }
}
