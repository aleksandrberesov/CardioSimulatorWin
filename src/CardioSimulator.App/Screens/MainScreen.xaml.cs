using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using DomainLanguage = CardioSimulator.Core.Domain.Language;
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
    private ConstructorViewModel? _constructorViewModel;
    private Func<Task<StorageFile?>>? _pickOpenZip;
    private Func<Task<StorageFile?>>? _pickSaveZip;
    private Func<Task<StorageFile?>>? _pickOpenImage;

    public MainScreen()
    {
        InitializeComponent();
        KeyDown += OnGlobalKeyDown;
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

        // Determine mode before creating ViewModels so we can pass the prefix.
        var modeId = appVm.SelectedOperatingMode.Id;
        var modePrefix = modeId.ToString().ToLowerInvariant();

        // Fresh per-mode view-models (Android keys them by mode id).
        _constructorViewModel = null;
        _monitorViewModel = new MonitorViewModel(appVm.Prefs, modePrefix);
        _rhythmViewModel = new RhythmViewModel(appVm.Repository, appVm.Prefs);

        Top.Bind(appVm, _monitorViewModel, OnStartStop);

        UIElement screen;
        Bottom.IsCompareVisible = modeId is OperatingMode.Teaching or OperatingMode.Testing or OperatingMode.Examination or OperatingMode.OSKE;

        switch (modeId)
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
                testing.Initialize(_monitorViewModel, _rhythmViewModel, appVm.SelectedLanguage);
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
                _constructorViewModel = constructorViewModel;
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
                screen = PlaceholderScreen(modeId.ToString());
                Bottom.PanelContent = null;
                break;
        }

        MiddleHost.Children.Clear();
        MiddleHost.Children.Add(screen);

        await _rhythmViewModel.LoadManifestAsync();
    }

    // ── Compare-rhythms dialog ─────────────────────────────────────────────

    /// <summary>
    /// Toggle compare mode: if already on, exit it. If off, show the picker/presets dialog.
    /// </summary>
    private async Task ShowCompareDialogAsync()
    {
        if (_rhythmViewModel is null || _monitorViewModel is null || _appViewModel is null) return;

        // If already in compare mode, exit it.
        if (_monitorViewModel.MonitorMode.IsCompareMode)
        {
            _monitorViewModel.ExitCompareMode();
            _rhythmViewModel.ClearComparisonWaveforms();
            _monitorViewModel.SetSeriesCount(12);
            _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
            return;
        }

        // Build dialog: pathology multi-select + preset list.
        var presets = _monitorViewModel.ComparisonPresets;
        var checks = new List<(string Id, CheckBox Cb)>();
        var pathologyStack = new StackPanel { Spacing = 4 };
        foreach (var r in _rhythmViewModel.Rhythms)
        {
            var label = _appViewModel.SelectedLanguage == DomainLanguage.RU
                ? (r.NameRu ?? r.TitleEn)
                : r.TitleEn;
            var cb = new CheckBox { Content = label };
            checks.Add((r.Id, cb));
            pathologyStack.Children.Add(cb);
        }

        var pathologyScroll = new ScrollViewer
        {
            Content = pathologyStack,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
            Width = 320,
        };

        var contentStack = new StackPanel { Spacing = 8 };
        contentStack.Children.Add(new TextBlock
        {
            Text = AppStrings.CompareSelectPathologies,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        contentStack.Children.Add(pathologyScroll);

        // Preset section — only shown when presets exist.
        TextBox? presetNameBox = null;
        if (presets.Count > 0)
        {
            contentStack.Children.Add(new TextBlock
            {
                Text = AppStrings.ComparePresets,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var presetPanel = new StackPanel { Spacing = 4 };
            foreach (var preset in presets)
            {
                var captured = preset;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var btn = new Button { Content = captured.Name };
                btn.Click += (_, _) =>
                {
                    // Pre-check the pathologies belonging to this preset.
                    foreach (var (id, cb) in checks)
                        cb.IsChecked = captured.PathologyIds.Contains(id);
                };
                var del = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0) };
                del.Click += (_, _) => _monitorViewModel.DeletePreset(captured.Name);
                row.Children.Add(btn);
                row.Children.Add(del);
                presetPanel.Children.Add(row);
            }
            contentStack.Children.Add(presetPanel);
        }

        // Save-as-preset input.
        presetNameBox = new TextBox { PlaceholderText = AppStrings.CompareSavePresetPlaceholder, Width = 320 };
        contentStack.Children.Add(new TextBlock
        {
            Text = AppStrings.CompareSavePresetLabel,
            Margin = new Thickness(0, 4, 0, 0),
        });
        contentStack.Children.Add(presetNameBox);

        var dialog = new ContentDialog
        {
            Title = AppStrings.CompareButton,
            Content = contentStack,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedIds = checks.Where(c => c.Cb.IsChecked == true).Select(c => c.Id).ToList();
        if (selectedIds.Count == 0) return;

        // Save preset if the user filled in a name.
        var presetName = presetNameBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(presetName))
            _monitorViewModel.SaveCurrentAsPreset(presetName, selectedIds, Lead.II);

        // Enter compare mode: collapse to single-lead view (Lead II) and load waveforms.
        _monitorViewModel.EnterCompareMode();
        _monitorViewModel.SetSeriesCount(1);
        _monitorViewModel.SetSeriesScheme(SeriesScheme.OneColumn);
        _rhythmViewModel.ClearComparisonWaveforms();
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

    // ── Keyboard Shortcuts (schema §2 Desktop Adaptations) ─────────────────
    // Space=Play/Stop, Ctrl+1-6=mode, Ctrl+Z/Y=Undo/Redo, Del=delete point, Esc=close overlays

    private void OnGlobalKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Skip when a text-entry control owns focus (don't intercept typing).
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox) return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space when !ctrl:
                if (_monitorViewModel is not null)
                {
                    var running = !_monitorViewModel.MonitorMode.IsRunning;
                    _monitorViewModel.SetIsRunning(running);
                    OnStartStop(running);
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number1 when ctrl:
            case Windows.System.VirtualKey.NumberPad1 when ctrl:
                SwitchToModeByIndex(0); e.Handled = true; break;
            case Windows.System.VirtualKey.Number2 when ctrl:
            case Windows.System.VirtualKey.NumberPad2 when ctrl:
                SwitchToModeByIndex(1); e.Handled = true; break;
            case Windows.System.VirtualKey.Number3 when ctrl:
            case Windows.System.VirtualKey.NumberPad3 when ctrl:
                SwitchToModeByIndex(2); e.Handled = true; break;
            case Windows.System.VirtualKey.Number4 when ctrl:
            case Windows.System.VirtualKey.NumberPad4 when ctrl:
                SwitchToModeByIndex(3); e.Handled = true; break;
            case Windows.System.VirtualKey.Number5 when ctrl:
            case Windows.System.VirtualKey.NumberPad5 when ctrl:
                SwitchToModeByIndex(4); e.Handled = true; break;
            case Windows.System.VirtualKey.Number6 when ctrl:
            case Windows.System.VirtualKey.NumberPad6 when ctrl:
                SwitchToModeByIndex(5); e.Handled = true; break;

            case Windows.System.VirtualKey.Z when ctrl:
            {
                var vm = _constructorViewModel;
                if (vm?.TargetFile is not null && vm.CanUndo(vm.FocusedLead))
                    vm.Undo(vm.FocusedLead);
                e.Handled = true;
                break;
            }
            case Windows.System.VirtualKey.Y when ctrl:
            {
                var vm = _constructorViewModel;
                if (vm?.TargetFile is not null && vm.CanRedo(vm.FocusedLead))
                    vm.Redo(vm.FocusedLead);
                e.Handled = true;
                break;
            }

            case Windows.System.VirtualKey.Delete:
            {
                var vm = _constructorViewModel;
                if (vm?.TargetFile is { } file)
                {
                    var idx = vm.SelectedIndex;
                    // Remove every significant point pinned to the current sample index.
                    var toRemove = file.SignificantPoints
                        .Where(p => p.Index == idx)
                        .Select(p => p.Type)
                        .ToList();
                    foreach (var t in toRemove)
                        vm.ToggleSignificantPoint(vm.FocusedLead, idx, t);
                }
                e.Handled = true;
                break;
            }

            case Windows.System.VirtualKey.Escape:
                // Esc is consumed here to signal screens to close their drawers/overlays.
                // Individual screens handle this via the ContentDialog's CloseButton.
                e.Handled = false; // let bubbling close any open ContentDialog
                break;
        }
    }

    private void SwitchToModeByIndex(int index)
    {
        if (_appViewModel is null) return;
        var modes = _appViewModel.OperatingModes;
        if (index < modes.Count) _appViewModel.UpdateOperatingMode(modes[index]);
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
