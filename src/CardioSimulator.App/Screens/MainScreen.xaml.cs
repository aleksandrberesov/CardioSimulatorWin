using System.ComponentModel;
using System.Linq;
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
    private Func<Task<StorageFile?>>? _pickOpenImage;

    public MainScreen()
    {
        InitializeComponent();
    }

    public void Initialize(
        AppViewModel appViewModel,
        Func<Task<StorageFile?>> pickOpenZip,
        Func<Task<StorageFile?>> pickSaveZip,
        Func<Task<StorageFile?>> pickOpenImage)
    {
        _appViewModel = appViewModel;
        _pickOpenZip = pickOpenZip;
        _pickSaveZip = pickSaveZip;
        _pickOpenImage = pickOpenImage;
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
        Bottom.IsCompareVisible = mode is OperatingMode.Teaching or OperatingMode.Testing or OperatingMode.Examination or OperatingMode.OSKE;

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
                teachingPanel.CompareClick += async (_, _) => await ShowCompareDialogAsync();
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

            case OperatingMode.Constructor:
                var constructorViewModel = new ConstructorViewModel(appVm.Repository);
                var constructor = new ConstructorScreen();
                constructor.Initialize(constructorViewModel, _monitorViewModel, _rhythmViewModel, appVm, _pickOpenImage);
                screen = constructor;
                Bottom.PanelContent = new ConstructorControlPanel(constructorViewModel, _monitorViewModel);
                break;

            case OperatingMode.CourseConstructor:
                var cc = new CourseConstructorScreen(_appViewModel.CourseConstructorViewModel, _appViewModel);
                screen = cc;
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

    /// <summary>Compare-rhythms dialog: multi-select pathologies, then overlay them on lead II.</summary>
    private async Task ShowCompareDialogAsync()
    {
        if (_rhythmViewModel is null || _monitorViewModel is null || _appViewModel is null) return;
        var checks = new List<(string Id, CheckBox Cb)>();
        var stack = new StackPanel { Spacing = 4 };
        foreach (var r in _rhythmViewModel.Rhythms)
        {
            var label = _appViewModel.SelectedLanguage == CardioSimulator.Core.Domain.Language.RU
                ? (r.NameRu ?? r.TitleEn)
                : r.TitleEn;
            var cb = new CheckBox { Content = label };
            checks.Add((r.Id, cb));
            stack.Children.Add(cb);
        }
        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
            Width = 320,
        };
        var dialog = new ContentDialog
        {
            Title = AppStrings.CompareButton,
            Content = scroll,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedIds = checks.Where(c => c.Cb.IsChecked == true).Select(c => c.Id).ToList();
        _rhythmViewModel.ClearComparisonWaveforms();
        if (selectedIds.Count == 0) return;

        // Collapse to single-lead view (Lead II) for the comparative overlay.
        _monitorViewModel.SetSeriesCount(1);
        _monitorViewModel.SetSeriesScheme(SeriesScheme.OneColumn);
        for (var i = 0; i < selectedIds.Count; i++)
        {
            await _rhythmViewModel.LoadComparisonWaveformAsync(i, selectedIds[i], Lead.II);
        }
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
