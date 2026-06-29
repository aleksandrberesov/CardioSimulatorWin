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
    private ConstructorViewModel? _constructorViewModel;
    private OperatingMode? _lastBuiltMode;
    private Func<Task<StorageFile?>>? _pickOpenZip;
    private Func<string, Task<StorageFile?>>? _pickSaveZip;
    private Func<Task<StorageFile?>>? _pickOpenImage;
    private Func<Task<StorageFile?>>? _pickOpenWfdb;
    private Func<Task<StorageFile?>>? _pickOpenJson;
    private Func<Task<StorageFile?>>? _pickSaveJson;

    public MainScreen()
    {
        InitializeComponent();
        KeyDown += OnGlobalKeyDown;
        // Keep the bottom bar exactly as tall as the top bar (the top hugs its content — logo +
        // selector pills — so its height is content-driven; mirror it onto the bottom strip).
        Top.SizeChanged += (_, e) => Bottom.Height = e.NewSize.Height;
    }

    public void Initialize(
        AppViewModel appViewModel,
        Func<Task<StorageFile?>> pickOpenZip,
        Func<string, Task<StorageFile?>> pickSaveZip,
        Func<Task<StorageFile?>> pickOpenImage,
        Func<Task<StorageFile?>> pickOpenWfdb,
        Func<Task<StorageFile?>> pickOpenJson,
        Func<Task<StorageFile?>> pickSaveJson)
    {
        _appViewModel = appViewModel;
        _pickOpenZip = pickOpenZip;
        _pickSaveZip = pickSaveZip;
        _pickOpenImage = pickOpenImage;
        _pickOpenWfdb = pickOpenWfdb;
        _pickOpenJson = pickOpenJson;
        _pickSaveJson = pickSaveJson;
        appViewModel.PropertyChanged += OnAppViewModelChanged;
        AppStrings.Changed += OnLanguageChanged;
        Bottom.SettingsClick += OnSettingsClick;
        Bottom.CompareClick += async (_, _) => await OnCompareToggleAsync();
        BuildForMode();
    }

    private void OnLanguageChanged() => BuildForMode();

    private void OnAppViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedOperatingMode))
        {
            BuildForMode();
        }
        else if (e.PropertyName is nameof(AppViewModel.SelectedCourseId) or nameof(AppViewModel.Courses))
        {
            // Re-apply the course-aware rhythm filter (Teaching mode only; harmless otherwise).
            if (_appViewModel is not null && _rhythmViewModel is not null &&
                _appViewModel.SelectedOperatingMode.Id == OperatingMode.Teaching)
            {
                _rhythmViewModel.SetCourseFilter(_appViewModel.SelectedCoursePathologies);
            }
        }
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

        // Customer: Teaching opens on "All rhythms" (the monitor) by default. Reset only when
        // entering the mode — not on a same-mode rebuild (e.g. a language change), which would
        // otherwise discard the course the user is reading.
        if (modeId == OperatingMode.Teaching && _lastBuiltMode != OperatingMode.Teaching)
            appVm.SelectCourse(null);
        _lastBuiltMode = modeId;

        Top.Bind(appVm, _rhythmViewModel);

        UIElement screen;
        // The bottom bar's separate compare button (the magnifier/zoom "+" tab) is hidden in every
        // mode: Teaching reaches compare via the monitor's own control panel, and Testing/Examination
        // are guided assessments where comparison is not offered.
        Bottom.IsCompareVisible = false;

        switch (modeId)
        {
            case OperatingMode.Teaching:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                _rhythmViewModel.SetCourseFilter(appVm.SelectedCoursePathologies);

                var teaching = new TeachingScreen();
                teaching.PaneTapped += async (_, idx) => await OnPaneTapAsync(idx);

                var teachingPanel = new MonitorControlPanel();
                teachingPanel.Bind(_monitorViewModel);
                teachingPanel.StartStopClick += (_, running) => OnStartStop(running);
                teachingPanel.CompareClick += async (_, _) => await OnCompareToggleAsync();
                // The three new panel options open over the monitor. Electrodes and 3D are modal
                // windows (ContentDialog, so they float above the native Win2D surface); pQRSt rides
                // on the bound view-model (SetShowImpulseLabels) so no host wiring is needed here.
                teachingPanel.ElectrodesClick += async (_, _) => await ElectrodesDialog.ShowAsync(XamlRoot, _monitorViewModel);
                teachingPanel.Heart3DClick += async (_, _) => await Heart3DDialog.ShowAsync(XamlRoot);
                // Recording-artifact noise rides on the view-model; the monitor regenerates the trace.
                teachingPanel.ArtifactSelected += (_, artifacts) => _monitorViewModel.SetArtifacts(artifacts);
                teachingPanel.EosClick += (_, _) => EosWindow.Toggle(XamlRoot);
                teachingPanel.TipsClick += (_, _) => TipsWindow.Toggle(XamlRoot);
                // Ruler/caliper: toggles the measurement overlay on the monitor surface.
                teachingPanel.RulerToggled += (_, active) => teaching.SetRulerActive(active);
                // The monitor controls only apply while the monitor is showing (the "All rhythms"
                // mode, or the monitor popped over a course); hidden while a course's lectures show.
                // Subscribe before Initialize so the initial all-rhythms auto-open is caught.
                teachingPanel.Visibility = Visibility.Collapsed;
                teaching.MonitorVisibilityChanged += (_, isOpen) =>
                {
                    teachingPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
                    if (!isOpen)
                    {
                        EosWindow.Close(); TipsWindow.Close(); // don't leave a panel floating over a course
                        teachingPanel.ResetRuler(); // sync the button when the monitor is dismissed
                    }
                };
                Bottom.PanelContent = teachingPanel;

                teaching.Initialize(_monitorViewModel, _rhythmViewModel, appVm);
                screen = teaching;
                break;

            case OperatingMode.Testing:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var testing = new TestingScreen();
                testing.Initialize(_monitorViewModel, _rhythmViewModel, appVm.TestRepository, appVm.SelectedLanguage);
                screen = testing;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.Examination:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var examination = new ExaminationScreen();
                examination.Initialize(
                    new ExaminationViewModel(appVm.ExamResultStore),
                    _monitorViewModel, _rhythmViewModel, appVm);
                screen = examination;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.OSKE:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var oske = new OSKEScreen();
                oske.Initialize(
                    new OskeViewModel(appVm.OskeRepository, appVm.OskeResultStore),
                    _monitorViewModel, _rhythmViewModel, appVm);
                screen = oske;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.Constructor:
                var constructorViewModel = new ConstructorViewModel(appVm.Repository);
                _constructorViewModel = constructorViewModel;
                var constructor = new ConstructorScreen();
                constructor.Initialize(constructorViewModel, _monitorViewModel, _rhythmViewModel, appVm, _pickOpenImage, _pickOpenWfdb);
                screen = constructor;
                Bottom.PanelContent = new ConstructorControlPanel(constructorViewModel, _monitorViewModel);
                break;

            case OperatingMode.CourseConstructor:
                var cc = new CourseConstructorScreen(_appViewModel.CourseConstructorViewModel, _appViewModel, _pickOpenImage);
                screen = cc;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.OskeConstructor:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var oskeCtor = new OskeConstructorScreen(
                    new OskeConstructorViewModel(appVm.OskeRepository),
                    _monitorViewModel, _rhythmViewModel, appVm);
                screen = oskeCtor;
                Bottom.PanelContent = null;
                break;

            case OperatingMode.TestConstructor:
                _monitorViewModel.SetSeriesCount(12);
                _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
                var testCtor = new TestConstructorScreen(
                    new TestConstructorViewModel(appVm.TestRepository, appVm.QuestionBank, appVm.Themes),
                    _monitorViewModel, _rhythmViewModel, appVm, _pickOpenImage!, _pickOpenJson!, _pickSaveJson!);
                screen = testCtor;
                // The "Question Bank" view toggle is hosted in the app top bar, beside the mode selector.
                Top.SetSubPanel(testCtor.QuestionBankButton);
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

    // ── Compare mode (per-pane targets) ────────────────────────────────────

    /// <summary>
    /// Compare-button handler. If already in compare mode, opens the manager (save/apply/exit).
    /// Otherwise enters compare mode — offering saved presets first, or seeding two default
    /// panes. Port of the Android <c>toggleCompareMode</c> + presets dialog flow.
    /// </summary>
    private async Task OnCompareToggleAsync()
    {
        if (_rhythmViewModel is null || _monitorViewModel is null || _appViewModel is null) return;

        if (_monitorViewModel.MonitorMode.IsCompareMode)
        {
            await ShowCompareManagerAsync();
            return;
        }

        var presets = _monitorViewModel.ComparisonPresets;
        if (presets.Count > 0)
        {
            await ShowEnterCompareDialogAsync(presets);
        }
        else
        {
            EnterCompareWithDefaults();
        }
    }

    /// <summary>
    /// Enters compare mode with the default layout: four single-column panes, only the first of
    /// which is pre-filled (the active rhythm, Lead II). The other three are tappable placeholders.
    /// </summary>
    private void EnterCompareWithDefaults()
    {
        var defaultId = _rhythmViewModel!.SelectedRhythm?.Id
                        ?? (_rhythmViewModel.Rhythms.Count > 0 ? _rhythmViewModel.Rhythms[0].Id : null);
        _monitorViewModel!.ToggleCompareMode(defaultId);
        _monitorViewModel.SetSeriesCount(4);
        _monitorViewModel.SetSeriesScheme(SeriesScheme.OneColumn);
    }

    /// <summary>Sizes the grid to fit the current comparison targets.</summary>
    private void ApplyCompareLayout()
    {
        var targets = _monitorViewModel!.MonitorMode.ComparisonTargets;
        var maxPane = targets.Count == 0 ? 1 : targets.Keys.Max();
        var count = Math.Clamp(maxPane + 1, 2, 12);
        _monitorViewModel.SetSeriesCount(count);
        _monitorViewModel.SetSeriesScheme(count <= 2 ? SeriesScheme.TwoColumn : SeriesScheme.Grid);
    }

    /// <summary>Exits compare mode and restores the default 12-lead grid.</summary>
    private void ExitCompare()
    {
        _monitorViewModel!.ExitCompareMode();
        _monitorViewModel.ClearComparisonTargets();
        _rhythmViewModel!.ClearComparisonWaveforms();
        _monitorViewModel.SetSeriesCount(12);
        _monitorViewModel.SetSeriesScheme(SeriesScheme.Grid);
    }

    /// <summary>Opens the per-pane target picker for <paramref name="pane"/> and applies the result.</summary>
    private async Task OnPaneTapAsync(int pane)
    {
        if (_rhythmViewModel is null || _monitorViewModel is null || _appViewModel is null) return;
        if (!_monitorViewModel.MonitorMode.IsCompareMode) return;

        var current = _monitorViewModel.MonitorMode.ComparisonTargets.GetValueOrDefault(pane);
        var target = await ComparisonTargetDialog.ShowAsync(
            XamlRoot,
            _rhythmViewModel.Rhythms,
            _appViewModel.SelectedLanguage,
            current?.PathologyId,
            current?.Lead);

        if (target is not null) _monitorViewModel.SetComparisonTarget(pane, target);
    }

    /// <summary>Entry dialog shown when presets exist: apply one or start a new comparison.</summary>
    private async Task ShowEnterCompareDialogAsync(IReadOnlyList<ComparisonPreset> presets)
    {
        var stack = new StackPanel { Spacing = 8, MinWidth = 280 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.ComparePresets,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var presetPanel = new StackPanel { Spacing = 4 };
        stack.Children.Add(presetPanel);

        var dialog = new ContentDialog
        {
            Title = AppStrings.CompareButton,
            Content = stack,
            PrimaryButtonText = AppStrings.CompareStartNew,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };

        ComparisonPreset? chosen = null;
        foreach (var preset in presets)
        {
            var captured = preset;
            var btn = new Button { Content = captured.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            btn.Click += (_, _) => { chosen = captured; dialog.Hide(); };
            presetPanel.Children.Add(btn);
        }

        var result = await dialog.ShowAsync();
        if (chosen is not null)
        {
            _monitorViewModel!.ApplyPreset(chosen);
            ApplyCompareLayout();
        }
        else if (result == ContentDialogResult.Primary)
        {
            EnterCompareWithDefaults();
        }
    }

    /// <summary>Manager dialog (while in compare mode): save a preset, apply/delete presets, or exit.</summary>
    private async Task ShowCompareManagerAsync()
    {
        if (_monitorViewModel is null) return;

        var stack = new StackPanel { Spacing = 8, MinWidth = 320 };
        var nameBox = new TextBox { PlaceholderText = AppStrings.CompareSavePresetPlaceholder };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.CompareSavePresetLabel,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        stack.Children.Add(nameBox);

        var presetPanel = new StackPanel { Spacing = 4 };
        stack.Children.Add(presetPanel);

        var dialog = new ContentDialog
        {
            Title = AppStrings.CompareModeTitle,
            Content = stack,
            PrimaryButtonText = AppStrings.CommonSave,
            SecondaryButtonText = AppStrings.CompareExit,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };

        var presets = _monitorViewModel.ComparisonPresets;
        if (presets.Count > 0)
        {
            presetPanel.Children.Add(new TextBlock
            {
                Text = AppStrings.ComparePresets,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var preset in presets)
            {
                var captured = preset;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var applyBtn = new Button { Content = captured.Name };
                applyBtn.Click += (_, _) => { _monitorViewModel.ApplyPreset(captured); ApplyCompareLayout(); dialog.Hide(); };
                var del = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0) };
                del.Click += (_, _) => { _monitorViewModel.DeletePreset(captured.Name); row.Visibility = Visibility.Collapsed; };
                row.Children.Add(applyBtn);
                row.Children.Add(del);
                presetPanel.Children.Add(row);
            }
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var name = nameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(name)) _monitorViewModel.SaveCurrentAsPreset(name);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            ExitCompare();
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
    // Space=Play/Stop, Ctrl+1-7=mode, Ctrl+Z/Y=Undo/Redo, Del=delete point, Esc=close overlays

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
            case Windows.System.VirtualKey.Number7 when ctrl:
            case Windows.System.VirtualKey.NumberPad7 when ctrl:
                SwitchToModeByIndex(6); e.Handled = true; break;

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
