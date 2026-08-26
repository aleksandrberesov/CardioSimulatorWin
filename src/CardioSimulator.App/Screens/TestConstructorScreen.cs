using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.UI;
using DomainLanguage = CardioSimulator.Core.Domain.Language;
using CourseSection = CardioSimulator.App.Data.CourseThemeCatalog.Section;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Standalone Test Constructor — its own top-level operating mode (parallel to the Course / OSCE
/// constructors). It hosts two views toggled in the toolbar:
/// <list type="bullet">
///   <item><b>Тесты</b> — author a self-assessment test (title, per-question time limit, ordered
///   questions), assembled by adding fresh questions or snapshotting from the bank.</item>
///   <item><b>Банк вопросов</b> — curate the standing question pool: create/edit/delete questions
///   (text / image / ECG stimulus, theme + tags), filter/search, and JSON import/export for
///   AI-generated batches.</item>
/// </list>
/// A live monitor preview of the selected ECG sits on the left with a start/stop control.
/// </summary>
/// <remarks>
/// Like <see cref="OskeConstructorScreen"/>, the monitor is built once and kept permanently parented
/// (toggled via running/visibility, never re-parented): the Win2D <c>EcgMonitorControl</c> tears itself
/// down on <c>Unloaded</c>, so re-parenting it would destroy it and crash the XAML layer. The two views
/// likewise swap by <see cref="UIElement.Visibility"/>, never by removing nodes from the tree.
/// </remarks>
public sealed class TestConstructorScreen : UserControl
{
    private enum View { Generator, Tests, Bank }

    /// <summary>Max acronyms addable to a generation filter (mirrors the prototype's rhythm cap).</summary>
    private const int MaxGenAcronyms = 6;

    private readonly TestConstructorViewModel _vm;
    private readonly MonitorViewModel _monitorVm;
    private readonly RhythmViewModel _rhythmVm;
    private readonly AppViewModel _appVm;
    private readonly MonitorView _monitor = new();
    private readonly Func<Task<StorageFile?>> _pickOpenImage;
    private readonly Func<Task<StorageFile?>> _pickOpenJson;
    private readonly Func<Task<StorageFile?>> _pickSaveJson;

    private View _view = View.Generator;

    private Button _generatorViewBtn = null!;
    private Button _testsViewBtn = null!;
    private Button _bankViewBtn = null!;
    private StackPanel _viewToggle = null!;
    private Grid _body = null!;
    // Full-width generator host: a fixed header on top, a fixed bank-stats + footer band on the bottom, and
    // only the ready-tests list in the middle scrolls (see RenderGenerator). This host itself does not scroll.
    private readonly Border _generatorHost = new()
    {
        Padding = new Thickness(16, 12, 16, 16),
        Visibility = Visibility.Collapsed,
    };

    // ── Generator (landing view) state ────────────────────────────────────────
    // Selected test-type keys ("questions" | "image" | "detect" | "assemble" | "clinical").
    private readonly HashSet<string> _genTypes = new() { "questions" };
    private readonly List<string> _genThemes = new();
    // Selected taxonomy acronym codes (canonical, upper-case) — the rhythm/pattern filter now works at the
    // acronym level (a rhythm exhibits one or more acronyms; questions inherit them via their bound rhythm
    // or a direct acronym tag).
    private readonly List<string> _genAcronyms = new();
    private bool _genOrMode = true;
    private int _genCount = 10;
    private int _genTime = 15;
    private bool _genWelcomed;

    // Toast overlay (bottom-right).
    private Border _toast = null!;
    private readonly TextBlock _toastTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _toastDesc = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;
    private StackPanel _bankToolbar = null!;
    private ToggleButton _startStop = null!;

    // Editor view: redesigned as a full-width tests-list-left / test-content-right layout (no monitor by
    // default), mirroring the generator. The right pane's status message persists across re-renders here.
    private string _editorStatus = string.Empty;

    private readonly ScrollViewer _editorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ScrollViewer _bankScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    // Full-width host for the redesigned bank browse (shown instead of the monitor+editor split while
    // browsing the bank; the split returns when a question is opened for editing).
    // Full-width bank browse host: a fixed header+filters row on top, a fixed pagination row on the bottom,
    // and only the middle item list scrolls (see BuildBankList). This host itself does not scroll.
    private readonly Border _bankBrowseHost = new()
    {
        Padding = new Thickness(16, 12, 16, 16),
        Visibility = Visibility.Collapsed,
    };
    private TextBlock _intro = null!;

    // Full-width Editor host: the tests list (left) and the selected test (right), each in its own scroll
    // viewer so the two sides scroll independently. The host is a two-column grid that fills the row.
    private Grid _editorViewHost = null!;
    private readonly ScrollViewer _editorListScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16, 12, 8, 16),
    };
    private readonly ScrollViewer _editorContentScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(8, 12, 16, 16),
    };

    // Bank browse filter state.
    private string? _bankThemeFilter;
    private string? _bankRhythmFilter;
    private readonly HashSet<string> _bankTypeFilters = new(); // empty = all types
    private string _bankSearch = string.Empty;
    private int _bankPage;
    private bool _bankWelcomed;
    private const int BankPageSize = 8;

    public TestConstructorScreen(
        TestConstructorViewModel vm,
        MonitorViewModel monitorVm,
        RhythmViewModel rhythmVm,
        AppViewModel appVm,
        Func<Task<StorageFile?>> pickOpenImage,
        Func<Task<StorageFile?>> pickOpenJson,
        Func<Task<StorageFile?>> pickSaveJson)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _pickOpenImage = pickOpenImage;
        _pickOpenJson = pickOpenJson;
        _pickSaveJson = pickSaveJson;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;

        Content = BuildLayout();
        _rhythmVm.PropertyChanged += OnRhythmChanged;
        _appVm.CourseRepository.ManifestChanged += OnCourseManifestChanged;
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            _rhythmVm.PropertyChanged -= OnRhythmChanged;
            _appVm.CourseRepository.ManifestChanged -= OnCourseManifestChanged;
            AppTheme.Changed -= OnThemeChanged;
            _toastTimer?.Stop();
        };
        ShowView(View.Generator);

        if (!_genWelcomed)
        {
            _genWelcomed = true;
            ShowToast("👋", AppStrings.TestGenWelcomeTitle, AppStrings.TestGenWelcomeDesc);
        }
    }

    private void OnThemeChanged()
    {
        if (_toast is not null)
        {
            _toast.Background = AppTheme.AppCardBackground;
            _toastDesc.Foreground = AppTheme.TextSecondary;
        }
        // Only the generator view is built from AppTheme brushes directly; the editor/bank use
        // themed WinUI controls that restyle themselves.
        if (_view == View.Generator) RenderGenerator();
    }

    /// <summary>
    /// The Tests | Bank view toggle group ("Tests" left of "Bank"). It is parented by the app top bar
    /// (see <see cref="MainScreen"/>) rather than this screen's toolbar, so it sits beside the mode
    /// selector. The active button is highlighted via <see cref="ShowView"/> regardless of where it is
    /// parented.
    /// </summary>
    public UIElement ViewToggle => _viewToggle;

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The ECG pickers depend on the rhythm manifest; rebuild once it loads.
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms)) RenderActiveView();
    }

    // ── Layout / toolbar ─────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(12, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // View toggle (Tests | Bank). The whole toggle group is hosted in the app top bar (see
        // MainScreen / TopControlPanel.SetSubPanel), beside the mode selector — not in this screen's
        // toolbar. "Tests" sits to the left of "Bank". The buttons are created/wired here and handed
        // out via ViewToggle; ShowView highlights the active one regardless of where it is parented.
        _generatorViewBtn = new Button { Content = AppStrings.TestGenView, VerticalAlignment = VerticalAlignment.Center };
        _generatorViewBtn.Click += (_, _) => ShowView(View.Generator);
        _testsViewBtn = new Button { Content = AppStrings.TestCtorViewTests, VerticalAlignment = VerticalAlignment.Center };
        _testsViewBtn.Click += (_, _) => ShowView(View.Tests);
        _bankViewBtn = new Button { Content = AppStrings.TestCtorViewBank, VerticalAlignment = VerticalAlignment.Center };
        _bankViewBtn.Click += (_, _) => ShowView(View.Bank);
        _viewToggle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _viewToggle.Children.Add(_generatorViewBtn);
        _viewToggle.Children.Add(_testsViewBtn);
        _viewToggle.Children.Add(_bankViewBtn);

        toolbar.Children.Add(BuildBankToolbar());

        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // Body: monitor (with start/stop overlay) on the left, swappable editor on the right.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var monitorHost = new Grid();
        monitorHost.Children.Add(_monitor);
        _startStop = new ToggleButton
        {
            Content = AppStrings.TestCtorStart,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            MinWidth = 76,
        };
        _startStop.Click += (_, _) => SetPreviewRunning(_startStop.IsChecked == true);
        monitorHost.Children.Add(_startStop);
        Grid.SetColumn(monitorHost, 0);
        body.Children.Add(monitorHost);

        _intro = new TextBlock
        {
            Text = AppStrings.TestCtorIntro,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360,
            Foreground = new SolidColorBrush(Colors.Gray),
        };
        var rightHost = new Grid();
        rightHost.Children.Add(_intro);
        rightHost.Children.Add(_editorScroll);
        rightHost.Children.Add(_bankScroll);
        Grid.SetColumn(rightHost, 1);
        body.Children.Add(rightHost);

        _body = body;

        // Full-width Editor host: tests list (left) + selected test (right), each independently scrollable.
        _editorViewHost = new Grid { Visibility = Visibility.Collapsed };
        _editorViewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _editorViewHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.9, GridUnitType.Star) });
        Grid.SetColumn(_editorListScroll, 0);
        Grid.SetColumn(_editorContentScroll, 1);
        _editorViewHost.Children.Add(_editorListScroll);
        _editorViewHost.Children.Add(_editorContentScroll);

        // Row 1 hosts the full-width generator, the Editor (two independent panes), the bank browse, and the
        // monitor+editor body (shown only while editing a bank question). Only one is visible at a time
        // (ShowView). The monitor is never re-parented — its body just goes Collapsed with the other views.
        var row1Host = new Grid();
        row1Host.Children.Add(body);
        row1Host.Children.Add(_generatorHost);
        row1Host.Children.Add(_editorViewHost);
        row1Host.Children.Add(_bankBrowseHost);
        Grid.SetRow(row1Host, 1);
        root.Children.Add(row1Host);

        root.Children.Add(BuildToast());
        Grid.SetRowSpan(_toast, 2);

        return root;
    }

    private Border BuildToast()
    {
        _toastDesc.Foreground = AppTheme.TextSecondary;
        var stack = new StackPanel();
        stack.Children.Add(_toastTitle);
        stack.Children.Add(_toastDesc);
        _toast = new Border
        {
            Child = stack,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 20, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 24),
            MaxWidth = 420,
            Visibility = Visibility.Collapsed,
        };
        return _toast;
    }

    private StackPanel BuildBankToolbar()
    {
        _bankToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

        var newBtn = new Button { Content = AppStrings.BankNewQuestion };
        newBtn.Click += (_, _) => { _vm.NewBankQuestion(); RenderBank(); };
        var importBtn = new Button { Content = AppStrings.BankImport };
        importBtn.Click += async (_, _) => await OnImportAsync();
        var exportBtn = new Button { Content = AppStrings.BankExport };
        exportBtn.Click += async (_, _) => await OnExportAsync();

        _bankToolbar.Children.Add(newBtn);
        _bankToolbar.Children.Add(importBtn);
        _bankToolbar.Children.Add(exportBtn);
        return _bankToolbar;
    }

    private void ShowView(View view)
    {
        _view = view;
        _generatorViewBtn.FontWeight = view == View.Generator ? FontWeights.Bold : FontWeights.Normal;
        _testsViewBtn.FontWeight = view == View.Tests ? FontWeights.Bold : FontWeights.Normal;
        _bankViewBtn.FontWeight = view == View.Bank ? FontWeights.Bold : FontWeights.Normal;
        // The Editor's controls live inline in its two-pane view, and the bank's actions live in the
        // redesigned browse panel, so the top-bar toolbars stay hidden.
        _bankToolbar.Visibility = Visibility.Collapsed;
        RenderActiveView();
    }

    private void RenderActiveView()
    {
        if (_view == View.Generator) RenderGenerator();
        else if (_view == View.Tests) RenderEditor();
        else RenderBank();
    }

    /// <summary>Toggles the row-1 hosts for the active view: the full-width generator, the full-width Editor
    /// (tests list + selected test), the full-width bank browse, or the monitor+editor split (used only while
    /// a bank question is open for editing). The monitor is never re-parented — its <c>body</c> just goes
    /// Collapsed with every view except bank editing.</summary>
    private void UpdateHostVisibility()
    {
        var bankEditing = _view == View.Bank && _vm.BankEdit is not null;
        _body.Visibility = bankEditing ? Visibility.Visible : Visibility.Collapsed;
        _generatorHost.Visibility = _view == View.Generator ? Visibility.Visible : Visibility.Collapsed;
        _editorViewHost.Visibility = _view == View.Tests ? Visibility.Visible : Visibility.Collapsed;
        _bankBrowseHost.Visibility = _view == View.Bank && !bankEditing ? Visibility.Visible : Visibility.Collapsed;
        if (!bankEditing) SetPreviewRunning(false); // the monitor only runs while editing a bank question now
    }

    // ── Monitor preview ───────────────────────────────────────────────────────

    private void RunPreview(string pathologyId)
    {
        // The monitor is only shown while editing a bank question; ignore preview requests from anywhere else
        // (the redesigned Editor view has no monitor by default).
        if (_view != View.Bank || _vm.BankEdit is null) return;
        _rhythmVm.SelectRhythm(pathologyId, persist: false);
        SetPreviewRunning(true);
    }

    private void SetPreviewRunning(bool run)
    {
        _monitorVm.SetIsRunning(run);
        _startStop.IsChecked = run;
        _startStop.Content = run ? AppStrings.TestCtorStop : AppStrings.TestCtorStart;
    }

    // ── Test editor ────────────────────────────────────────────────────────--

    /// <summary>Deletes a test (the open one clears the editor too). Shared by the tests-list rows and the
    /// open test's Delete button.</summary>
    private async Task OnEditorDeleteTestAsync(string testId)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.TestCtorDelete,
            Content = AppStrings.TestCtorDeleteConfirm,
            PrimaryButtonText = AppStrings.TestCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // _vm.Delete() removes the open test and clears the edit model; Repository.DeleteTest handles the rest.
        var ok = testId == _vm.TestId ? _vm.Delete() : _vm.Repository.DeleteTest(testId);
        if (ok) { _editorStatus = string.Empty; RenderEditor(); }
    }

    /// <summary>
    /// Renders the Editor view into its two full-width, independently-scrolling panes: the list of saved
    /// tests on the left, the selected test's content (settings + question cards) on the right. Rebuilt whole
    /// on each change (add/remove/save/select), reading straight from the view-model.
    /// </summary>
    private void RenderEditor()
    {
        UpdateHostVisibility();
        _editorListScroll.Content = EditorTestsListCard();
        _editorContentScroll.Content = EditorContentCard();
    }

    /// <summary>Left pane: the saved tests, each selectable (click to open) with the open one highlighted,
    /// plus a "New test" action and a delete per row.</summary>
    private UIElement EditorTestsListCard()
    {
        var tests = _vm.Repository.Tests;
        var stack = new StackPanel { Spacing = 8 };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = AppStrings.TestCtorTestsLabel, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(CountBadge(tests.Count));
        Grid.SetColumn(titleRow, 0);
        header.Children.Add(titleRow);
        var newBtn = PrimaryButton(AppStrings.TestCtorNew);
        newBtn.VerticalAlignment = VerticalAlignment.Center;
        newBtn.Click += (_, _) => { _vm.NewTest(); _editorStatus = string.Empty; RenderEditor(); };
        Grid.SetColumn(newBtn, 1);
        header.Children.Add(newBtn);
        stack.Children.Add(header);

        if (tests.Count == 0)
            stack.Children.Add(new TextBlock { Text = AppStrings.TestGenReadyEmpty, FontSize = 13, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        else
            foreach (var t in tests) stack.Children.Add(EditorTestRow(t));

        return GenCard(stack);
    }

    private UIElement EditorTestRow(CardioSimulator.Core.Domain.Test test)
    {
        var isOpen = _vm.TestId == test.TestId;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(test.Title) ? test.TestId : test.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap });
        var minutes = test.QuestionTimeSeconds > 0 ? (int)Math.Round(test.QuestionTimeSeconds * test.Questions.Count / 60.0) : 0;
        info.Children.Add(new TextBlock
        {
            Text = minutes > 0 ? AppStrings.TestGenReadyMetaFormat(test.Questions.Count, minutes) : AppStrings.TestGenReadyUntimedFormat(test.Questions.Count),
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
        });

        // Clicking the row (a flat, borderless button) opens the test; the delete button sits beside it so
        // its click never doubles as a "load".
        var id = test.TestId;
        var loadBtn = new Button
        {
            Content = info,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        loadBtn.Click += (_, _) => { if (_vm.Load(id)) { _editorStatus = string.Empty; RenderEditor(); } };
        Grid.SetColumn(loadBtn, 0);
        row.Children.Add(loadBtn);

        var del = new Button { Content = "🗑️", Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(del, AppStrings.BankDelete);
        del.Click += async (_, _) => await OnEditorDeleteTestAsync(id);
        Grid.SetColumn(del, 1);
        row.Children.Add(del);

        return new Border
        {
            Child = row,
            Background = isOpen ? AppTheme.AppAccentSoftBackground : AppTheme.AppCardBackground,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(isOpen ? 4 : 1, isOpen ? 0 : 1, isOpen ? 0 : 1, isOpen ? 0 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
        };
    }

    /// <summary>Right pane: the selected test's settings (title / per-question time), Save / Delete, and its
    /// ordered question cards; a placeholder prompt when no test is open.</summary>
    private UIElement EditorContentCard()
    {
        if (!_vm.HasTest)
            return GenCard(new TextBlock
            {
                Text = AppStrings.TestCtorIntro,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420,
                Margin = new Thickness(0, 48, 0, 48),
            });

        var stack = new StackPanel { Spacing = 12 };

        var settings = new Grid { ColumnSpacing = 12 };
        settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBox = new TextBox { Header = AppStrings.TestCtorTitleLabel, Text = _vm.Title, HorizontalAlignment = HorizontalAlignment.Stretch, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        titleBox.TextChanged += (_, _) => { _vm.Title = titleBox.Text; _vm.IsDirty = true; };
        Grid.SetColumn(titleBox, 0);
        settings.Children.Add(titleBox);
        var timeBox = new TextBox { Header = AppStrings.TestCtorTimeLabel, Text = _vm.QuestionTimeSeconds.ToString(), Width = 130, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        timeBox.TextChanged += (_, _) => { _vm.QuestionTimeSeconds = int.TryParse(timeBox.Text, out var s) && s >= 0 ? s : 0; _vm.IsDirty = true; };
        Grid.SetColumn(timeBox, 1);
        settings.Children.Add(timeBox);
        stack.Children.Add(settings);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var save = PrimaryButton(AppStrings.TestCtorSave);
        save.Click += async (_, _) =>
        {
            if (!await EnsureAssembliesBuiltAsync(_vm.Questions)) return;
            if (_vm.Save()) { _editorStatus = AppStrings.TestCtorSaved; RenderEditor(); }
        };
        actionRow.Children.Add(save);
        var delete = new Button { Content = AppStrings.TestCtorDelete };
        delete.Click += async (_, _) => await OnEditorDeleteTestAsync(_vm.TestId!);
        actionRow.Children.Add(delete);
        if (!string.IsNullOrEmpty(_editorStatus))
            actionRow.Children.Add(new TextBlock { Text = _editorStatus, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Foreground = AppTheme.TextSecondary });
        stack.Children.Add(actionRow);

        stack.Children.Add(GenHairline());

        for (var i = 0; i < _vm.Questions.Count; i++)
            stack.Children.Add(BuildQuestionCard(_vm.Questions[i], i + 1, RenderEditor, inTest: true));

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var add = new Button { Content = AppStrings.TestCtorAddQuestion };
        add.Click += (_, _) => { _vm.AddQuestion(); RenderEditor(); };
        addRow.Children.Add(add);
        var addFromBank = new Button { Content = AppStrings.TestCtorAddFromBank };
        addFromBank.Click += async (_, _) => await OnAddFromBankAsync();
        addRow.Children.Add(addFromBank);
        stack.Children.Add(addRow);

        return GenCard(stack);
    }

    // ── Bank view ─────────────────────────────────────────────────────────────

    private void RenderBank()
    {
        UpdateHostVisibility();

        if (_vm.BankEdit is { } editing)
        {
            // Editing a question: the monitor+editor split. The card lives in the right column.
            _intro.Visibility = Visibility.Collapsed;
            _editorScroll.Visibility = Visibility.Collapsed;
            _bankScroll.Visibility = Visibility.Visible;

            var panel = new StackPanel { Spacing = 12, Padding = new Thickness(12, 8, 12, 8) };
            panel.Children.Add(BuildQuestionCard(editing, number: null, RenderBank, inTest: false));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = AppStrings.BankSave };
            save.Click += async (_, _) =>
            {
                if (_vm.BankEdit is { } be && !await EnsureAssembliesBuiltAsync(new[] { be })) return;
                if (_vm.SaveBankQuestion()) RenderBank();
            };
            var cancel = new Button { Content = AppStrings.CommonCancel };
            cancel.Click += (_, _) => { _vm.CancelBankEdit(); RenderBank(); };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            _bankScroll.Content = panel;

            if (editing.IsAssembly && !string.IsNullOrWhiteSpace(editing.AssembleSourceId)) RunPreview(editing.AssembleSourceId!);
            else if (editing.Kind == QuestionStimulus.Ecg && !string.IsNullOrWhiteSpace(editing.PathologyId)) RunPreview(editing.PathologyId!);
            else SetPreviewRunning(false);
            return;
        }

        // Browsing: the full-width redesigned bank («Банк вопросов»).
        _bankBrowseHost.Child = BuildBankList();
        SetPreviewRunning(false);

        if (!_bankWelcomed)
        {
            _bankWelcomed = true;
            ShowToast("👋", AppStrings.Bank2WelcomeTitle, AppStrings.Bank2WelcomeDescFormat(_vm.Bank.Questions.Count));
        }
    }

    private StackPanel? _bankItemsHost;
    private Grid? _bankPaginationHost;
    private readonly List<(string Key, Border Border, TextBlock Label)> _bankTypeTags = new();

    // Bank card semantic colours (constant across themes — data-viz accents on the card surface).
    private static readonly Color BankCorrectColor = Color.FromArgb(0xFF, 0x1A, 0x8A, 0x6A);
    private static readonly Color BankTopicColor = Color.FromArgb(0xFF, 0x1A, 0x6A, 0x5A);
    private static readonly Color BankRhythmColor = Color.FromArgb(0xFF, 0x7A, 0x3A, 0x7A);
    private static readonly Color BankTagColor = Color.FromArgb(0xFF, 0x4A, 0x7A, 0x9A);
    private static readonly Color DiffEasyColor = Color.FromArgb(0xFF, 0x1A, 0x8A, 0x6A);
    private static readonly Color DiffMediumColor = Color.FromArgb(0xFF, 0xB0, 0x8A, 0x2A);
    private static readonly Color DiffHardColor = Color.FromArgb(0xFF, 0xB0, 0x3A, 0x3A);

    private UIElement BuildBankList()
    {
        // Full-width, three-band layout: header + filters pinned on top (row 0), the item list in a scroll
        // viewer that takes the remaining height (row 1), and the pagination pinned on the bottom (row 2).
        // Only the middle band scrolls, so the search/filters and the page controls stay put.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Spacing = 16 };
        top.Children.Add(BankHeader());
        top.Children.Add(BankFilters());
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        _bankItemsHost = new StackPanel { Spacing = 12 };
        var itemsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 12, 12, 12),
            Content = _bankItemsHost,
        };
        Grid.SetRow(itemsScroll, 1);
        root.Children.Add(itemsScroll);

        _bankPaginationHost = new Grid();
        var paginationBar = new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(4, 12, 4, 0),
            Child = _bankPaginationHost,
        };
        Grid.SetRow(paginationBar, 2);
        root.Children.Add(paginationBar);

        RefreshBankItems();
        return root;
    }

    private UIElement BankHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = AppStrings.TestGenOpenBank, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent });
        titles.Children.Add(new TextBlock { Text = AppStrings.Bank2Subtitle, FontSize = 14, Foreground = AppTheme.TextSecondary });
        Grid.SetColumn(titles, 0);
        grid.Children.Add(titles);

        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, VerticalAlignment = VerticalAlignment.Center };
        stats.Children.Add(BankStat(_vm.Bank.Questions.Count.ToString("N0"), AppStrings.Bank2StatQuestions));
        stats.Children.Add(BankStat(CourseSections().Count(s => !s.IsSub).ToString("N0"), AppStrings.Bank2StatThemes));
        stats.Children.Add(BankStat(_rhythmVm.Rhythms.Count.ToString("N0"), AppStrings.Bank2StatRhythms));
        var statsChip = new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(40),
            Padding = new Thickness(24, 6, 24, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stats,
        };
        Grid.SetColumn(statsChip, 1);
        grid.Children.Add(statsChip);
        return grid;

        UIElement BankStat(string number, string label)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            s.Children.Add(new TextBlock { Text = number, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center });
            s.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
            return s;
        }
    }

    private UIElement BankFilters()
    {
        var stack = new StackPanel { Spacing = 12 };

        // Row 1 — search + actions.
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox
        {
            PlaceholderText = AppStrings.Bank2SearchPlaceholder,
            Text = _bankSearch,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        search.TextChanged += (_, _) => { _bankSearch = search.Text; _bankPage = 0; RefreshBankItems(); };
        Grid.SetColumn(search, 0);
        searchRow.Children.Add(search);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var import = new Button { Content = AppStrings.BankImport };
        import.Click += async (_, _) => await OnImportAsync();
        var export = new Button { Content = AppStrings.BankExport };
        export.Click += async (_, _) => await OnExportAsync();
        var create = PrimaryButton(AppStrings.BankNewQuestion);
        create.Click += (_, _) => { _vm.NewBankQuestion(); RenderBank(); };
        actions.Children.Add(import);
        actions.Children.Add(export);
        actions.Children.Add(create);
        Grid.SetColumn(actions, 1);
        searchRow.Children.Add(actions);
        stack.Children.Add(searchRow);

        // Row 2 — section/theme + rhythm.
        var mainRow = new Grid();
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var themeGroup = new StackPanel { Spacing = 4 };
        themeGroup.Children.Add(new TextBlock { Text = AppStrings.Bank2SectionLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        var themeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        themeCombo.Items.Add(new ComboBoxItem { Content = AppStrings.BankFilterAll, Tag = null });
        foreach (var s in CourseSections())
            themeCombo.Items.Add(new ComboBoxItem { Content = s.Display, Tag = s.Value });
        themeCombo.SelectedItem = themeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == _bankThemeFilter) ?? themeCombo.Items[0];
        themeCombo.SelectionChanged += (_, _) => { _bankThemeFilter = (themeCombo.SelectedItem as ComboBoxItem)?.Tag as string; _bankPage = 0; RefreshBankItems(); };
        themeGroup.Children.Add(themeCombo);
        Grid.SetColumn(themeGroup, 0);
        mainRow.Children.Add(themeGroup);

        var rhythmGroup = new StackPanel { Spacing = 4 };
        rhythmGroup.Children.Add(new TextBlock { Text = AppStrings.TestGenRhythmLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        var rhythmCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        rhythmCombo.Items.Add(new ComboBoxItem { Content = AppStrings.Bank2AllRhythms, Tag = null });
        var acrCounts = AcronymBankCounts();
        foreach (var code in RhythmAcronyms().OrderBy(AcronymLabel, StringComparer.CurrentCultureIgnoreCase))
        {
            var inBank = acrCounts.GetValueOrDefault(code);
            var item = new ComboBoxItem { Content = $"{AcronymLabel(code)}  ·  {inBank}", Tag = code };
            if (inBank == 0)
            {
                item.Foreground = AppTheme.TextSecondary;
            }
            rhythmCombo.Items.Add(item);
        }
        rhythmCombo.SelectedItem = rhythmCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == _bankRhythmFilter) ?? rhythmCombo.Items[0];
        rhythmCombo.SelectionChanged += (_, _) => { _bankRhythmFilter = (rhythmCombo.SelectedItem as ComboBoxItem)?.Tag as string; _bankPage = 0; RefreshBankItems(); };
        rhythmGroup.Children.Add(rhythmCombo);
        Grid.SetColumn(rhythmGroup, 2);
        mainRow.Children.Add(rhythmGroup);
        stack.Children.Add(mainRow);

        // Row 3 — question-type tags.
        stack.Children.Add(GenHairline());
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        typeRow.Children.Add(new TextBlock { Text = AppStrings.Bank2TypeLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        var tags = new WrapPanel { HSpacing = 6, VSpacing = 6 };
        _bankTypeTags.Clear();
        AddTypeTag(tags, "all", AppStrings.Bank2TypeAll);
        AddTypeTag(tags, "image", AppStrings.Bank2TypeImage);
        AddTypeTag(tags, "detect", AppStrings.Bank2TypeDetect);
        AddTypeTag(tags, "assemble", AppStrings.Bank2TypeAssemble);
        AddTypeTag(tags, "case", AppStrings.Bank2TypeCase);
        ApplyTypeTagStyles();
        typeRow.Children.Add(tags);
        stack.Children.Add(typeRow);

        return GenCard(stack);
    }

    private void AddTypeTag(Panel host, string key, string label)
    {
        var text = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var border = new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(14, 3, 14, 3),
            BorderThickness = new Thickness(2),
        };
        border.PointerPressed += (_, _) =>
        {
            if (key == "all") _bankTypeFilters.Clear();
            else if (!_bankTypeFilters.Remove(key)) _bankTypeFilters.Add(key);
            _bankPage = 0;
            ApplyTypeTagStyles();
            RefreshBankItems();
        };
        _bankTypeTags.Add((key, border, text));
        host.Children.Add(border);
    }

    private void ApplyTypeTagStyles()
    {
        foreach (var (key, border, label) in _bankTypeTags)
        {
            var active = key == "all" ? _bankTypeFilters.Count == 0 : _bankTypeFilters.Contains(key);
            border.Background = active ? AppTheme.Accent : AppTheme.AppCardBackground;
            border.BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder;
            label.Foreground = active ? new SolidColorBrush(Colors.White) : AppTheme.TextSecondary;
        }
    }

    private void RefreshBankItems()
    {
        if (_bankItemsHost is null) return;
        _bankItemsHost.Children.Clear();

        var matches = FilteredBankQuestions();
        var total = matches.Count;
        var pages = Math.Max(1, (total + BankPageSize - 1) / BankPageSize);
        _bankPage = Math.Clamp(_bankPage, 0, pages - 1);

        if (total == 0)
        {
            _bankItemsHost.Children.Add(new TextBlock { Text = AppStrings.BankEmpty, Foreground = AppTheme.TextSecondary, Margin = new Thickness(0, 12, 0, 0) });
        }
        else
        {
            var pageItems = matches.Skip(_bankPage * BankPageSize).Take(BankPageSize).ToList();
            for (var i = 0; i < pageItems.Count; i++)
                _bankItemsHost.Children.Add(BuildBankListItem(pageItems[i], _bankPage * BankPageSize + i + 1));
        }

        RefreshPagination(total, pages);
    }

    private void RefreshPagination(int total, int pages)
    {
        if (_bankPaginationHost is null) return;
        _bankPaginationHost.Children.Clear();
        _bankPaginationHost.ColumnDefinitions.Clear();
        _bankPaginationHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bankPaginationHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var shown = total == 0 ? 0 : Math.Min(BankPageSize, total - _bankPage * BankPageSize);
        var info = new TextBlock { Text = AppStrings.Bank2PaginationFormat(shown, total), FontSize = 13, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 0);
        _bankPaginationHost.Children.Add(info);

        if (pages > 1)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var p in PageWindow(_bankPage, pages))
            {
                if (p < 0)
                {
                    row.Children.Add(new TextBlock { Text = "…", Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                    continue;
                }
                var active = p == _bankPage;
                var btn = new Button
                {
                    Content = (p + 1).ToString(),
                    MinWidth = 38,
                    Background = active ? AppTheme.Accent : AppTheme.AppCardBackground,
                    Foreground = active ? new SolidColorBrush(Colors.White) : AppTheme.TextPrimary,
                    BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder,
                };
                var target = p;
                btn.Click += (_, _) => { _bankPage = target; RefreshBankItems(); };
                row.Children.Add(btn);
            }
            Grid.SetColumn(row, 1);
            _bankPaginationHost.Children.Add(row);
        }
    }

    /// <summary>Page indices to show, with -1 marking an ellipsis gap (windowed when there are many pages).</summary>
    private static IEnumerable<int> PageWindow(int current, int pages)
    {
        if (pages <= 9)
        {
            for (var i = 0; i < pages; i++) yield return i;
            yield break;
        }
        var set = new SortedSet<int> { 0, pages - 1, current };
        for (var d = 1; d <= 2; d++)
        {
            if (current - d >= 0) set.Add(current - d);
            if (current + d < pages) set.Add(current + d);
        }
        var prev = -2;
        foreach (var p in set)
        {
            if (p - prev > 1) yield return -1;
            yield return p;
            prev = p;
        }
    }

    private static string BankCategory(TestQuestion q) =>
        q.IsAssembly ? "assemble"
        : q.Stimulus == QuestionStimulus.Image ? "image"
        : q.Stimulus == QuestionStimulus.Ecg ? "detect"
        : "case";

    private IReadOnlyList<TestQuestion> FilteredBankQuestions()
    {
        const StringComparison oic = StringComparison.CurrentCultureIgnoreCase;
        IEnumerable<TestQuestion> q = _vm.Bank.Questions;
        if (!string.IsNullOrWhiteSpace(_bankThemeFilter))
            q = q.Where(x => string.Equals(x.Theme, _bankThemeFilter, oic));
        if (!string.IsNullOrWhiteSpace(_bankRhythmFilter))
        {
            var normCode = Taxonomy.Normalize(_bankRhythmFilter);
            if (normCode is not null)
            {
                var expandedRhythmIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in _rhythmVm.Rhythms)
                    if (r.AcronymList.Any(a => Taxonomy.Normalize(a) == normCode))
                        expandedRhythmIds.Add(r.Id);

                q = q.Where(x =>
                    (x.PathologyId is { } p && expandedRhythmIds.Contains(p)) ||
                    x.AcronymList.Any(a => Taxonomy.Normalize(a) == normCode));
            }
        }
        if (_bankTypeFilters.Count > 0)
            q = q.Where(x => _bankTypeFilters.Contains(BankCategory(x)));
        if (!string.IsNullOrWhiteSpace(_bankSearch))
        {
            var needle = _bankSearch.Trim();
            q = q.Where(x =>
                x.Text.Contains(needle, oic) ||
                x.Id.Contains(needle, oic) ||
                (x.Theme is { } th && th.Contains(needle, oic)) ||
                x.TagList.Any(t => t.Contains(needle, oic)) ||
                x.AcronymList.Any(a => a.Contains(needle, oic)) ||
                (x.PathologyId is { } pid && (pid.Contains(needle, oic) || EcgLabel(pid).Contains(needle, oic))));
        }
        return q.ToList();
    }

    private UIElement BuildBankListItem(TestQuestion q, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var main = new StackPanel { Spacing = 6 };

        // Header: #index · id · type badge · difficulty.
        var header = new WrapPanel { HSpacing = 10, VSpacing = 4 };
        var idText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        idText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"#{index}  " });
        idText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = AppStrings.TestCtorIdFormat(q.Id), Foreground = AppTheme.TextSecondary, FontWeight = FontWeights.Normal });
        header.Children.Add(idText);
        header.Children.Add(StimulusChip(q));
        if (q.Difficulty is { } diff) header.Children.Add(SoftColorBadge(AppStrings.DifficultyLabel(diff), DifficultyColor(diff)));
        main.Children.Add(header);

        // Text.
        main.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(q.Text) ? q.Id : q.Text,
            FontSize = 14,
            Foreground = AppTheme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
        });

        // Stimulus placeholder (image / ECG / assembly).
        if (StimulusPlaceholder(q) is { } placeholder)
            main.Children.Add(new Border
            {
                Background = AppTheme.AppSubtleFill,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new TextBlock { Text = placeholder, FontSize = 12, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center },
            });

        // Meta tags: theme + acronyms + rhythm + tags.
        var meta = new WrapPanel { HSpacing = 6, VSpacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        if (!string.IsNullOrWhiteSpace(q.Theme)) meta.Children.Add(SoftColorBadge("📖 " + q.Theme, BankTopicColor));
        foreach (var a in q.AcronymList) meta.Children.Add(SoftColorBadge("🔖 " + a, AppTheme.AccentColor));
        if (q.PathologyId is { } rid) meta.Children.Add(SoftColorBadge($"💓 {rid} — {EcgLabel(rid)}", BankRhythmColor));
        foreach (var t in q.TagList) meta.Children.Add(SoftColorBadge("#" + t, BankTagColor));
        if (meta.Children.Count > 0) main.Children.Add(meta);

        // Answers preview.
        if (q.IsAssembly)
        {
            main.Children.Add(new TextBlock { Text = AppStrings.Bank2StimulusAssemble, FontSize = 13, FontStyle = Windows.UI.Text.FontStyle.Italic, Foreground = AppTheme.TextSecondary, Margin = new Thickness(0, 2, 0, 0) });
        }
        else if (q.Options.Count > 0)
        {
            var answers = new Grid { Margin = new Thickness(0, 2, 0, 0), ColumnSpacing = 20 };
            answers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            answers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rows = (q.Options.Count + 1) / 2;
            for (var r = 0; r < rows; r++) answers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < q.Options.Count; i++)
            {
                var opt = q.Options[i];
                var correct = opt.Id == q.CorrectOptionId;
                var t = new TextBlock
                {
                    Text = (correct ? "✅ " : "• ") + opt.Text,
                    FontSize = 13,
                    Foreground = correct ? new SolidColorBrush(BankCorrectColor) : AppTheme.TextSecondary,
                    FontWeight = correct ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(t, i % 2);
                Grid.SetRow(t, i / 2);
                answers.Children.Add(t);
            }
            main.Children.Add(answers);
        }

        Grid.SetColumn(main, 0);
        grid.Children.Add(main);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        var edit = new Button { Content = "✏️", Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(edit, AppStrings.BankEdit);
        edit.Click += (_, _) => { if (_vm.EditBankQuestion(q.Id)) RenderBank(); };
        var del = new Button { Content = "🗑️", Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(del, AppStrings.BankDelete);
        del.Click += async (_, _) => await OnDeleteBankQuestionAsync(q);
        actions.Children.Add(edit);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            Child = grid,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
        };
    }

    private static string? StimulusPlaceholder(TestQuestion q) =>
        q.IsAssembly ? AppStrings.Bank2StimulusAssemble
        : q.Stimulus == QuestionStimulus.Image ? AppStrings.Bank2StimulusImage
        : q.Stimulus == QuestionStimulus.Ecg ? AppStrings.Bank2StimulusEcg
        : null;

    private static Color DifficultyColor(QuestionDifficulty d) => d switch
    {
        QuestionDifficulty.Easy => DiffEasyColor,
        QuestionDifficulty.Hard => DiffHardColor,
        _ => DiffMediumColor,
    };

    private static Border SoftColorBadge(string text, Color color) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)),
        CornerRadius = new CornerRadius(30),
        Padding = new Thickness(10, 2, 10, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(color), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 },
    };

    private async Task OnDeleteBankQuestionAsync(TestQuestion q)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.BankDelete,
            Content = AppStrings.BankDeleteConfirm,
            PrimaryButtonText = AppStrings.BankDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_vm.DeleteBankQuestion(q.Id)) RenderBank();
    }

    // ── Shared question card ───────────────────────────────────────────────────

    private UIElement BuildQuestionCard(TestConstructorViewModel.EditQuestion q, int? number, Action reRender, bool inTest)
    {
        var card = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = number is { } n ? AppStrings.TestCtorQuestionLabelFormat(n) : AppStrings.BankNewQuestion,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleStack.Children.Add(new TextBlock { Text = AppStrings.TestCtorIdFormat(q.Id), Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);
        if (inTest)
        {
            var removeQ = new Button { Content = AppStrings.TestCtorRemoveQuestion };
            removeQ.Click += (_, _) => { _vm.RemoveQuestion(q); reRender(); };
            Grid.SetColumn(removeQ, 1);
            header.Children.Add(removeQ);
        }
        card.Children.Add(header);

        var text = MakeMultilineBox(AppStrings.TestCtorQuestionText, q.Text);
        text.TextChanged += (_, _) => { q.Text = text.Text; _vm.IsDirty = true; };
        card.Children.Add(text);

        // Question type: text / image / ECG / assemble.
        card.Children.Add(BuildStimulusSelector(q, reRender));
        if (q.IsAssembly)
        {
            card.Children.Add(BuildAssembleEditor(q, reRender));
        }
        else
        {
            switch (q.Stimulus())
            {
                case QuestionStimulus.Ecg:
                    card.Children.Add(BuildEcgPicker(q));
                    break;
                case QuestionStimulus.Image:
                    card.Children.Add(BuildImagePicker(q, reRender));
                    break;
            }
        }

        // Theme + tags.
        card.Children.Add(BuildThemeTagsRow(q));

        // Options — only for single-choice questions (an assembly is graded on the assembled strip).
        if (!q.IsAssembly)
        {
            card.Children.Add(new TextBlock { Text = AppStrings.TestCtorCorrect, Opacity = 0.7, FontSize = 12 });
            for (var i = 0; i < q.Options.Count; i++)
                card.Children.Add(BuildOptionRow(q, q.Options[i], i + 1, reRender));
            var addOpt = new Button { Content = "+", MinWidth = 36, IsEnabled = q.Options.Count < TestConstructorViewModel.MaxOptions };
            addOpt.Click += (_, _) => { _vm.AddOption(q); reRender(); };
            card.Children.Add(addOpt);
        }

        var comment = MakeMultilineBox(AppStrings.TestCtorComment, q.Comment);
        comment.TextChanged += (_, _) => { q.Comment = comment.Text; _vm.IsDirty = true; };
        card.Children.Add(comment);

        if (inTest)
        {
            var toBank = new Button { Content = AppStrings.TestCtorToBank, HorizontalAlignment = HorizontalAlignment.Left };
            toBank.Click += async (_, _) =>
            {
                if (!await EnsureAssembliesBuiltAsync(new[] { q })) return;
                if (_vm.SaveQuestionToBank(q)) { _editorStatus = AppStrings.TestCtorSavedToBank; reRender(); }
            };
            card.Children.Add(toBank);
        }

        return card;
    }

    private UIElement BuildStimulusSelector(TestConstructorViewModel.EditQuestion q, Action reRender)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = AppStrings.TestCtorStimulusLabel, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 });

        var group = "stimulus_" + q.Id;
        RadioButton Make(string label, QuestionStimulus kind)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = !q.IsAssembly && q.Kind == kind, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            rb.Checked += (_, _) =>
            {
                if (!q.IsAssembly && q.Kind == kind) return;
                q.IsAssembly = false;
                q.Kind = kind;
                _vm.IsDirty = true;
                reRender();
            };
            return rb;
        }
        stack.Children.Add(Make(AppStrings.TestCtorStimulusText, QuestionStimulus.Text));
        stack.Children.Add(Make(AppStrings.TestCtorStimulusImage, QuestionStimulus.Image));
        stack.Children.Add(Make(AppStrings.TestCtorStimulusEcg, QuestionStimulus.Ecg));

        var assemble = new RadioButton
        {
            Content = AppStrings.TestCtorStimulusAssemble,
            GroupName = group,
            IsChecked = q.IsAssembly,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        assemble.Checked += (_, _) =>
        {
            if (q.IsAssembly) return;
            q.IsAssembly = true;
            _vm.IsDirty = true;
            reRender();
        };
        stack.Children.Add(assemble);
        return stack;
    }

    private UIElement BuildImagePicker(TestConstructorViewModel.EditQuestion q, Action reRender)
    {
        var stack = new StackPanel { Spacing = 6 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pick = new Button { Content = AppStrings.TestCtorPickImage };
        pick.Click += async (_, _) =>
        {
            var file = await _pickOpenImage();
            if (file is null) return;
            var rel = TestImageStore.Copy(file.Path, q.Id);
            if (rel is not null)
            {
                q.ImagePath = rel;
                q.PathologyId = null;
                _vm.IsDirty = true;
                reRender();
            }
        };
        row.Children.Add(pick);
        if (!string.IsNullOrWhiteSpace(q.ImagePath))
        {
            var remove = new Button { Content = AppStrings.TestCtorRemoveImage };
            remove.Click += (_, _) => { q.ImagePath = null; _vm.IsDirty = true; reRender(); };
            row.Children.Add(remove);
        }
        stack.Children.Add(row);

        if (TestImageStore.UriFor(q.ImagePath) is { } uri)
        {
            stack.Children.Add(new Image
            {
                Source = new BitmapImage(uri),
                MaxHeight = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform,
            });
        }
        return stack;
    }

    private UIElement BuildThemeTagsRow(TestConstructorViewModel.EditQuestion q)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var themeCombo = new ComboBox { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center, Header = AppStrings.TestCtorTheme };
        themeCombo.Items.Add(new ComboBoxItem { Content = AppStrings.BankThemeNone, Tag = null });
        foreach (var s in CourseSections())
            themeCombo.Items.Add(new ComboBoxItem { Content = s.Display, Tag = s.Value });
        themeCombo.SelectedItem = themeCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, q.Theme, StringComparison.CurrentCultureIgnoreCase)) ?? themeCombo.Items[0];
        themeCombo.SelectionChanged += (_, _) => { q.Theme = (themeCombo.SelectedItem as ComboBoxItem)?.Tag as string; _vm.IsDirty = true; };
        Grid.SetColumn(themeCombo, 0);
        grid.Children.Add(themeCombo);

        // Difficulty (optional; drives the badge in the bank browse).
        var difficulty = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center, Header = AppStrings.Bank2DifficultyLabel };
        difficulty.Items.Add(new ComboBoxItem { Content = AppStrings.DiffUnset, Tag = null });
        difficulty.Items.Add(new ComboBoxItem { Content = AppStrings.DiffEasy, Tag = QuestionDifficulty.Easy });
        difficulty.Items.Add(new ComboBoxItem { Content = AppStrings.DiffMedium, Tag = QuestionDifficulty.Medium });
        difficulty.Items.Add(new ComboBoxItem { Content = AppStrings.DiffHard, Tag = QuestionDifficulty.Hard });
        difficulty.SelectedItem = difficulty.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as QuestionDifficulty?) == q.Difficulty) ?? difficulty.Items[0];
        difficulty.SelectionChanged += (_, _) => { q.Difficulty = (difficulty.SelectedItem as ComboBoxItem)?.Tag as QuestionDifficulty?; _vm.IsDirty = true; };
        Grid.SetColumn(difficulty, 1);
        grid.Children.Add(difficulty);

        var tags = MakeTextBox(AppStrings.TestCtorTags, 0);
        tags.Header = AppStrings.TestCtorTags;
        tags.PlaceholderText = AppStrings.TestCtorTagsPlaceholder;
        tags.HorizontalAlignment = HorizontalAlignment.Stretch;
        tags.Text = string.Join(", ", q.Tags);
        tags.TextChanged += (_, _) =>
        {
            q.Tags = tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            _vm.IsDirty = true;
        };
        Grid.SetColumn(tags, 2);
        grid.Children.Add(tags);

        var outer = new StackPanel { Spacing = 10 };
        outer.Children.Add(grid);
        outer.Children.Add(BuildSubsectionPicker(q));
        outer.Children.Add(BuildAcronymPicker(q));
        return outer;
    }

    /// <summary>
    /// The course-subsection picker for a question: a dropdown of the loaded course's Темы/Подтемы that
    /// carry a <c>subsection:</c> key. Picking one gives the question a <em>direct</em> Learning-Scale
    /// join key (e.g. «1.2»), so a graded answer rolls up onto that course section even when the question
    /// carries no taxonomy acronym — the only way theory sections (Раздел 1 etc.) can be scored, since the
    /// acronym dictionary doesn't cover them. Complements <see cref="BuildAcronymPicker"/>.
    /// </summary>
    private UIElement BuildSubsectionPicker(TestConstructorViewModel.EditQuestion q)
    {
        var combo = new ComboBox
        {
            Header = AppStrings.TestCtorSubsection,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.Items.Add(new ComboBoxItem { Content = AppStrings.TestCtorSubsectionNone, Tag = null });
        foreach (var opt in CourseSubsections())
            combo.Items.Add(new ComboBoxItem { Content = opt.Indented, Tag = opt.Key });

        // Keep a previously-picked key selectable even if the course it came from was edited/removed, so
        // reopening the test never silently drops the mapping.
        var current = q.Subsection?.Trim();
        var match = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.IsNullOrEmpty(current))
        {
            match = new ComboBoxItem { Content = current, Tag = current };
            combo.Items.Add(match);
        }
        combo.SelectedItem = match ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            q.Subsection = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            _vm.IsDirty = true;
        };
        return combo;
    }

    /// <summary>
    /// The canonical-acronym picker for a question: an autosuggest over the <see cref="Taxonomy"/>
    /// (match by code or Russian name) that adds removable chips. These acronyms are the join key that
    /// routes a graded answer into course-subsection/section mastery on the Learning Scale, so tagging a
    /// question here is what makes it "count" toward a student's results.
    /// </summary>
    private UIElement BuildAcronymPicker(TestConstructorViewModel.EditQuestion q)
    {
        var panel = new StackPanel { Spacing = 6 };

        var suggest = new AutoSuggestBox
        {
            Header = AppStrings.TestCtorAcronyms,
            PlaceholderText = AppStrings.TestCtorAcronymsPlaceholder,
            QueryIcon = new SymbolIcon(Symbol.Add),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var chipsScroll = new ScrollViewer
        {
            Content = chips,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        };

        void RebuildChips()
        {
            chips.Children.Clear();
            if (q.Acronyms.Count == 0)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = AppStrings.TestCtorAcronymsNone,
                    FontSize = 12,
                    Foreground = AppTheme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }
            foreach (var a in q.Acronyms.ToList())
            {
                var captured = a;
                chips.Children.Add(AcronymChip(captured, () =>
                {
                    q.Acronyms.RemoveAll(x => string.Equals(x, captured, StringComparison.OrdinalIgnoreCase));
                    _vm.IsDirty = true;
                    RebuildChips();
                }));
            }
        }

        void Add(string? token)
        {
            var norm = Taxonomy.Normalize(token);
            if (norm is null || !Taxonomy.Shared.Contains(norm)) return; // only real taxonomy codes
            if (!q.Acronyms.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)))
            {
                q.Acronyms.Add(norm);
                _vm.IsDirty = true;
                RebuildChips();
            }
            suggest.Text = string.Empty;
        }

        suggest.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var needle = suggest.Text.Trim();
            var chosen = new HashSet<string>(q.Acronyms, StringComparer.OrdinalIgnoreCase);
            suggest.ItemsSource = Taxonomy.Shared.Entries
                .Where(x => !chosen.Contains(x.Acronym))
                .Where(x => needle.Length == 0
                    || x.Acronym.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || x.NameRu.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .Select(x => $"{x.Acronym} — {x.NameRu}")
                .ToList();
        };
        suggest.QuerySubmitted += (_, e) =>
        {
            var token = (e.ChosenSuggestion as string) ?? e.QueryText;
            Add(token.Split('—')[0].Trim()); // strip the " — name" the suggestion shows
        };

        panel.Children.Add(suggest);
        panel.Children.Add(chipsScroll);
        RebuildChips();
        return panel;
    }

    /// <summary>A removable chip for one linked acronym; muted/red styling flags a code that is no
    /// longer in the taxonomy (e.g. from a legacy import) so the author can drop it.</summary>
    private UIElement AcronymChip(string acronym, Action onRemove)
    {
        var entry = Taxonomy.Shared.Find(acronym);
        var known = entry is not null;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = acronym,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(label, known ? $"{entry!.Acronym} · {entry.NameRu} · §{entry.Subsection}" : acronym);
        row.Children.Add(label);

        var remove = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(5, 0, 5, 0),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => onRemove();
        row.Children.Add(remove);

        return new Border
        {
            Child = row,
            Background = AppTheme.AppSubtleFill,
            BorderBrush = known ? AppTheme.Accent : new SolidColorBrush(Color.FromArgb(0xFF, 0xD6, 0x6A, 0x6A)),
            BorderThickness = new Thickness(known ? 1 : 2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 3, 4, 3),
        };
    }

    private UIElement BuildOptionRow(TestConstructorViewModel.EditQuestion q, TestConstructorViewModel.EditOption opt, int number, Action reRender)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var radio = new RadioButton
        {
            GroupName = "correct_" + q.Id,
            IsChecked = q.CorrectOptionId == opt.Id,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
        };
        radio.Checked += (_, _) => { q.CorrectOptionId = opt.Id; _vm.IsDirty = true; };
        Grid.SetColumn(radio, 0);
        row.Children.Add(radio);

        var box = MakeTextBox(AppStrings.TestCtorOptionFormat(number), 0);
        box.Text = opt.Text;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.TextChanged += (_, _) => { opt.Text = box.Text; _vm.IsDirty = true; };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var remove = new Button
        {
            Content = "✕",
            MinWidth = 36,
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = q.Options.Count > TestConstructorViewModel.MinOptions,
        };
        remove.Click += (_, _) => { _vm.RemoveOption(q, opt); reRender(); };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    /// <summary>
    /// ECG stimulus picker — the canonical <see cref="RhythmPickerButton"/> (grouped-and-searchable
    /// dropdown, same UI as the Teaching left drawer), with a clear (✕) button restoring "None".
    /// </summary>
    private UIElement BuildEcgPicker(TestConstructorViewModel.EditQuestion q)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = AppStrings.TestCtorEcg, VerticalAlignment = VerticalAlignment.Center });

        var picker = new RhythmPickerButton
        {
            DisplayLanguage = _appVm.SelectedLanguage,
            PlaceholderText = AppStrings.TestCtorEcgNone,
            ClearTooltip = AppStrings.TestCtorEcgNone,
            MinWidth = 240,
            SelectedId = q.PathologyId,
        };
        picker.SetRhythms(_rhythmVm.Rhythms);
        picker.SelectionChanged += (_, entry) =>
        {
            q.PathologyId = entry?.Id;
            q.ImagePath = null;
            _vm.IsDirty = true;
            if (entry is not null) RunPreview(entry.Id);
            else SetPreviewRunning(false);
        };
        stack.Children.Add(picker);

        return stack;
    }

    // ── «Собери ЭКГ» authoring ────────────────────────────────────────────────

    private UIElement BuildAssembleEditor(TestConstructorViewModel.EditQuestion q, Action reRender)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.AssembleCtorHint,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // Parameters as a 2-column form (labels | controls), one control per row. The control column
        // is star-sized so every field shrinks with the (narrow, ~40%-wide) editor pane instead of
        // overflowing it. Keeping Lead and Parts on their own rows — rather than side-by-side on a
        // single horizontal row — is what stops the "Частей" selector from being clipped off the right
        // edge on high-DPI / non-maximized layouts where the pane is too narrow for a combined row.
        var form = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 3; r++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddFormRow(int row, string label, FrameworkElement control)
        {
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            form.Children.Add(lbl);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        // Row 0: source rhythm picker. Left-aligned + MaxWidth caps it on wide panes and lets it
        // shrink (ellipsizing long names) on narrow ones, so it never pushes past the pane either.
        var sourcePicker = new RhythmPickerButton
        {
            DisplayLanguage = _appVm.SelectedLanguage,
            PlaceholderText = AppStrings.AssembleCtorNone,
            ClearTooltip = AppStrings.AssembleCtorNone,
            MinWidth = 200,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            SelectedId = q.AssembleSourceId,
        };
        sourcePicker.SetRhythms(_rhythmVm.Rhythms);
        sourcePicker.SelectionChanged += (_, entry) =>
        {
            q.AssembleSourceId = entry?.Id;
            if (q.AssembleSourceId is { } pid) RunPreview(pid); else SetPreviewRunning(false);
            RebuildAssembly(q);
            _vm.IsDirty = true;
            reRender();
        };
        AddFormRow(0, AppStrings.AssembleCtorSource, sourcePicker);

        // Row 1: lead selection.
        var leadCombo = new ComboBox { MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var lead in Leads.All)
            leadCombo.Items.Add(new ComboBoxItem { Content = lead.ToString(), Tag = lead });
        leadCombo.SelectedItem = leadCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (Lead)i.Tag == q.AssembleLead) ?? leadCombo.Items.Cast<ComboBoxItem>().First();
        leadCombo.SelectionChanged += (_, _) =>
        {
            if ((leadCombo.SelectedItem as ComboBoxItem)?.Tag is Lead l && l != q.AssembleLead)
            {
                q.AssembleLead = l;
                RebuildAssembly(q);
                _vm.IsDirty = true;
                reRender();
            }
        };
        AddFormRow(1, AppStrings.AssembleCtorLead, leadCombo);

        // Row 2: number of parts — a whole strip split into a handful of pieces (4 or 5), not a dense
        // row of cells. An older question saved with a different count keeps that value as an extra
        // option so simply opening it in the editor doesn't silently re-slice it.
        var partsCombo = new ComboBox { MinWidth = 70, HorizontalAlignment = HorizontalAlignment.Left };
        var partChoices = new List<int> { 4, 5 };
        if (!partChoices.Contains(q.AssemblePartCount)
            && q.AssemblePartCount is >= EcgAssemblySlicer.MinParts and <= EcgAssemblySlicer.MaxParts)
        {
            partChoices.Add(q.AssemblePartCount);
            partChoices.Sort();
        }
        foreach (var n in partChoices)
            partsCombo.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        partsCombo.SelectedItem = partsCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (int)i.Tag == q.AssemblePartCount) ?? partsCombo.Items[0];
        partsCombo.SelectionChanged += (_, _) =>
        {
            if ((partsCombo.SelectedItem as ComboBoxItem)?.Tag is int n && n != q.AssemblePartCount)
            {
                q.AssemblePartCount = n;
                RebuildAssembly(q);
                _vm.IsDirty = true;
                reRender();
            }
        };
        AddFormRow(2, AppStrings.AssembleCtorParts, partsCombo);

        stack.Children.Add(form);

        // Status: how many parts are ready, or why not.
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        if (q.Assembly is { IsComplete: true } built)
        {
            status.Text = AppStrings.AssembleCtorBuiltFormat(built.PartCount);
            status.Foreground = AppTheme.Accent;
        }
        else if (!string.IsNullOrWhiteSpace(q.AssembleSourceId))
        {
            status.Text = AppStrings.AssembleCtorBuildFailed;
            status.Foreground = AppTheme.Negative;
        }
        else
        {
            status.Text = AppStrings.AssembleCtorHint;
            status.Foreground = new SolidColorBrush(Colors.Gray);
            status.Opacity = 0.6;
        }
        stack.Children.Add(status);

        return stack;
    }

    /// <summary>Re-slices a «Собери ЭКГ» question's source into parts (null source → clears the spec).</summary>
    private void RebuildAssembly(TestConstructorViewModel.EditQuestion q)
    {
        if (string.IsNullOrWhiteSpace(q.AssembleSourceId))
        {
            q.Assembly = null;
            return;
        }
        var fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
        q.Assembly = EcgAssemblyBuilder.Build(_appVm.Repository, q.AssembleSourceId!, q.AssembleLead, q.AssemblePartCount, fs);
    }

    /// <summary>
    /// Slices any assembly questions that are not yet built (or were invalidated by an edit) before a
    /// save. Returns false — and warns — if any source rhythm can't be sliced, so nothing is half-saved.
    /// </summary>
    private async Task<bool> EnsureAssembliesBuiltAsync(IEnumerable<TestConstructorViewModel.EditQuestion> questions)
    {
        var anyFailed = false;
        foreach (var q in questions.Where(q => q.IsAssembly))
        {
            if (q.Assembly is not { IsComplete: true }) RebuildAssembly(q);
            if (q.Assembly is not { IsComplete: true }) anyFailed = true;
        }
        if (anyFailed)
        {
            await InfoDialogAsync(AppStrings.TestCtorStimulusAssemble, AppStrings.AssembleCtorBuildFailed);
            return false;
        }
        return true;
    }

    private static UIElement StimulusChip(TestQuestion q) =>
        q.IsAssembly ? ChipLabel(AppStrings.TestCtorStimulusAssemble) : StimulusChip(q.Stimulus);

    private static UIElement StimulusChip(QuestionStimulus stimulus) => ChipLabel(stimulus switch
    {
        QuestionStimulus.Image => AppStrings.TestCtorStimulusImage,
        QuestionStimulus.Ecg => AppStrings.TestCtorStimulusEcg,
        _ => AppStrings.TestCtorStimulusText,
    });

    private static Border ChipLabel(string label) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(30, 0x33, 0xA0, 0x6A)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 0x33, 0xA0, 0x6A)) },
    };

    /// <summary>Localized display name for a rhythm entry already in hand — O(1), no lookup.</summary>
    private string EcgLabel(PathologyEntry entry) =>
        _appVm.SelectedLanguage == DomainLanguage.RU ? (entry.ResolvedNameRu ?? entry.TitleEn) : entry.TitleEn;

    /// <summary>Localized display name by id. Prefer the <see cref="PathologyEntry"/> overload inside a
    /// loop over <see cref="RhythmViewModel.Rhythms"/> — this one scans the list, so calling it per item
    /// is O(n²) (that quadratic froze the assembly editor's two full-catalog combos on large datasets).</summary>
    private string EcgLabel(string id)
    {
        var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == id);
        return entry is null ? id : EcgLabel(entry);
    }

    // Course-derived section list (Course → Тема + their Подтемы), cached until the course manifest reloads
    // or the UI language changes. The Theme/Section pickers mirror the sections and sub-topics that actually
    // exist in the loaded course package instead of a hand-managed catalog.
    private List<CourseSection>? _courseSectionsCache;
    private DomainLanguage? _courseSectionsCacheLang;
    private List<CourseSubsectionOption>? _courseSubsectionsCache;
    private DomainLanguage? _courseSubsectionsCacheLang;

    private void OnCourseManifestChanged(object? sender, EventArgs e)
    {
        _courseSectionsCache = null;
        _courseSubsectionsCache = null;
    }

    /// <summary>
    /// The Theme/Section catalog from the loaded course package(s) — see <see cref="CourseThemeCatalog"/>.
    /// Cached here until <see cref="CourseRepository.ManifestChanged"/> fires or the language changes, since
    /// building it reads and parses each course.
    /// </summary>
    private IReadOnlyList<CourseSection> CourseSections()
    {
        var lang = _appVm.SelectedLanguage;
        if (_courseSectionsCache is not null && _courseSectionsCacheLang == lang) return _courseSectionsCache;

        var sections = CourseThemeCatalog.Sections(_appVm.CourseRepository, lang).ToList();
        _courseSectionsCache = sections;
        _courseSectionsCacheLang = lang;
        return sections;
    }

    /// <summary>One pickable course-subsection node — its <c>subsection:</c> key (the Learning-Scale join
    /// key) plus a display label. Sub-topics are indented under their Тема in the dropdown.</summary>
    private readonly record struct CourseSubsectionOption(string Key, string Label, bool IsSub)
    {
        public string Indented => IsSub ? "    ↳ " + Label : Label;
    }

    /// <summary>
    /// The distinct <c>subsection:</c>-mapped Темы/Подтемы across every loaded course, in teaching-
    /// navigation order and localized to the UI language. These are exactly the nodes the Learning Scale
    /// scores, so a question tagged with one of these keys lands on the matching section/subtopic. Cached
    /// like <see cref="CourseSections"/> (invalidated on manifest reload or language change).
    /// </summary>
    private IReadOnlyList<CourseSubsectionOption> CourseSubsections()
    {
        var lang = _appVm.SelectedLanguage;
        if (_courseSubsectionsCache is not null && _courseSubsectionsCacheLang == lang) return _courseSubsectionsCache;

        var ru = lang == DomainLanguage.RU;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<CourseSubsectionOption>();

        void Add(string? subsection, string name, bool isSub)
        {
            var key = subsection?.Trim();
            if (string.IsNullOrEmpty(key) || !seen.Add(key)) return;
            var label = string.IsNullOrWhiteSpace(name) ? key : $"{key} — {name}";
            options.Add(new CourseSubsectionOption(key, label, isSub));
        }

        // Prefer the node's explicit subsection:, else fall back to numbering carried in its title —
        // resolved identically to the Learning-Scale map (LearningScaleViewModel.ResolveKey) so a picked
        // key lands on the matching section/subtopic there.
        foreach (var entry in _appVm.CourseRepository.Courses)
        {
            if (_appVm.CourseRepository.ReadCourse(entry.Id) is not { } course) continue;
            foreach (var topic in course.Topics)
            {
                var topicName = CourseTopicFlyout.TopicName(topic, ru);
                Add(topic.Subsection ?? CourseNumbering.NumberPrefix(topicName), topicName, isSub: false);
                foreach (var lec in CourseTopicFlyout.Subtopics(course, topic.Id))
                {
                    var lecName = CourseTopicFlyout.LectureName(lec, ru);
                    Add(lec.Subsection ?? CourseNumbering.NumberPrefix(lecName), lecName, isSub: true);
                }
            }
            foreach (var lec in CourseTopicFlyout.UngroupedLectures(course))
            {
                var lecName = CourseTopicFlyout.LectureName(lec, ru);
                Add(lec.Subsection ?? CourseNumbering.NumberPrefix(lecName), lecName, isSub: true);
            }
        }

        _courseSubsectionsCache = options;
        _courseSubsectionsCacheLang = lang;
        return options;
    }

    /// <summary>The distinct canonical taxonomy acronyms exhibited by the loaded rhythms (the union of every
    /// <see cref="PathologyEntry.AcronymList"/>), upper-cased and de-duplicated. This is the item source for
    /// the generator's rhythm/pattern filter — selection happens at the acronym level, not per rhythm.</summary>
    private IReadOnlyList<string> RhythmAcronyms()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new List<string>();
        foreach (var r in _rhythmVm.Rhythms)
            foreach (var a in r.AcronymList)
                if (Taxonomy.Normalize(a) is { } code && seen.Add(code)) codes.Add(code);
        return codes;
    }

    /// <summary>Dropdown/chip label for an acronym: <c>CODE — name</c>, where the name is the taxonomy's
    /// Russian name in RU and its English name otherwise. Falls back to the bare code when the taxonomy has
    /// no entry or no localized name.</summary>
    private string AcronymLabel(string code)
    {
        var entry = Taxonomy.Shared.Find(code);
        var name = (_appVm.SelectedLanguage == DomainLanguage.RU ? entry?.NameRu : entry?.NameEn)?.Trim();
        return string.IsNullOrEmpty(name) ? code : $"{code} — {name}";
    }    /// <summary>
    /// How many bank questions each acronym is present in — a question counts toward an acronym if it is
    /// directly tagged with it or its bound rhythm exhibits it.
    /// </summary>
    private Dictionary<string, int> AcronymBankCounts()
    {
        var rhythmAcr = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in _rhythmVm.Rhythms)
        {
            var list = new List<string>();
            foreach (var a in r.AcronymList)
                if (Taxonomy.Normalize(a) is { } n) list.Add(n);
            if (list.Count > 0) rhythmAcr[r.Id] = list;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in _vm.Bank.Questions)
        {
            var acrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in q.AcronymList)
                if (Taxonomy.Normalize(a) is { } n) acrs.Add(n);
            if (q.PathologyId is { } p && rhythmAcr.TryGetValue(p, out var inherited))
                foreach (var n in inherited) acrs.Add(n);
            foreach (var n in acrs) counts[n] = counts.GetValueOrDefault(n) + 1;
        }
        return counts;
    }


    // ── Add from bank ───────────────────────────────────────────────────────--

    private async Task OnAddFromBankAsync()
    {
        var questions = _vm.Bank.Questions;
        if (questions.Count == 0)
        {
            await InfoDialogAsync(AppStrings.TestCtorAddFromBank, AppStrings.BankEmpty);
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 360 };
        foreach (var q in questions)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(q.Theme)) meta.Add(q.Theme!);
            meta.Add(AppStrings.TestCtorIdFormat(q.Id));
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(q.Text) ? q.Id : q.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
            item.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), Opacity = 0.7, FontSize = 12 });
            list.Items.Add(new ListViewItem { Content = item, Tag = q });
        }

        var dialog = new ContentDialog
        {
            Title = AppStrings.TestCtorAddFromBank,
            Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = AppStrings.TestCtorAddSelected,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var added = 0;
        foreach (var item in list.SelectedItems.OfType<ListViewItem>())
            if (item.Tag is TestQuestion q) { _vm.AddFromBank(q); added++; }
        if (added > 0) RenderEditor();
    }

    // ── Import / export ─────────────────────────────────────────────────────--

    private async Task OnImportAsync()
    {
        var file = await _pickOpenJson();
        if (file is null) return;
        try
        {
            var json = await FileIO.ReadTextAsync(file);
            var questions = CardioSimulator.Core.Data.TestJson.DeserializeBank(json);
            // Give any question missing an id a fresh one so it is addressable.
            var normalized = questions
                .Select(q => string.IsNullOrWhiteSpace(q.Id) ? q with { Id = TestConstructorViewModel.NewId() } : q)
                .ToList();
            var count = _vm.Bank.Import(normalized);
            await InfoDialogAsync(AppStrings.BankImport, AppStrings.BankImportedFormat(count));
            RenderBank();
        }
        catch
        {
            await InfoDialogAsync(AppStrings.BankImport, AppStrings.BankImportFailed);
        }
    }

    private async Task OnExportAsync()
    {
        var file = await _pickSaveJson();
        if (file is null) return;
        try
        {
            await FileIO.WriteTextAsync(file, _vm.Bank.ExportAll());
            await InfoDialogAsync(AppStrings.BankExport, AppStrings.BankExported);
        }
        catch
        {
            await InfoDialogAsync(AppStrings.BankExport, AppStrings.BankExportFailed);
        }
    }

    private async Task InfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        await dialog.ShowAsync();
    }

    private static TextBox MakeTextBox(string placeholder, double minWidth)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            VerticalAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        if (minWidth > 0) box.MinWidth = minWidth;
        return box;
    }

    private static TextBox MakeMultilineBox(string placeholder, string text) => new()
    {
        PlaceholderText = placeholder,
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  Generator landing view («Конструктор тестов» — build a test in 2 steps)
    // ══════════════════════════════════════════════════════════════════════════

    private void StartNewTestInEditor()
    {
        _vm.NewTest();
        _editorStatus = string.Empty;
        ShowView(View.Tests); // → RenderEditor
    }

    private void EditTestInEditor(string testId)
    {
        if (!_vm.Load(testId)) return;
        _editorStatus = string.Empty;
        ShowView(View.Tests); // → RenderEditor
    }

    private void RenderGenerator()
    {
        UpdateHostVisibility();
        // Full width, three-band layout (matching the Bank): the header pinned on top, the main area
        // (ready-tests list + constructor) filling the middle, and the bank stats + footer pinned on the
        // bottom. Only the ready-tests list scrolls (see GenMainGrid).
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = GenHeader();
        Grid.SetRow((FrameworkElement)header, 0);
        root.Children.Add(header);

        var main = GenMainGrid();
        Grid.SetRow((FrameworkElement)main, 1);
        ((FrameworkElement)main).Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(main);

        var bottom = new StackPanel { Spacing = 16, Margin = new Thickness(0, 16, 0, 0) };
        bottom.Children.Add(GenBankStats());
        bottom.Children.Add(GenFooter());
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        _generatorHost.Child = root;
    }

    private UIElement GenHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = AppStrings.TestGenTitle, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent });
        titles.Children.Add(new TextBlock { Text = AppStrings.TestGenSubtitle, FontSize = 14, Foreground = AppTheme.TextSecondary });
        Grid.SetColumn(titles, 0);
        grid.Children.Add(titles);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var bankBtn = new Button { Content = AppStrings.TestGenOpenBank };
        bankBtn.Click += (_, _) => ShowView(View.Bank);
        var newBtn = PrimaryButton(AppStrings.TestGenNew);
        newBtn.Click += (_, _) => StartNewTestInEditor();
        actions.Children.Add(bankBtn);
        actions.Children.Add(newBtn);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return grid;
    }

    private UIElement GenMainGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        // Only the ready-tests list scrolls; the constructor gets an Auto scroll purely as a safety net so
        // its Generate button never clips on a short window (no scrollbar appears while it fits).
        var left = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0),
            Content = GenReadyTestsCard(),
        };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 0, 0, 0),
            Content = GenConstructorCard(),
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private UIElement GenReadyTestsCard()
    {
        var tests = _vm.Repository.Tests;
        var stack = new StackPanel { Spacing = 8 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new TextBlock { Text = AppStrings.TestGenReady, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(CountBadge(tests.Count));
        stack.Children.Add(title);

        if (tests.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = AppStrings.TestGenReadyEmpty, FontSize = 13, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        }
        else
        {
            foreach (var t in tests)
                stack.Children.Add(GenTestItem(t));
        }

        return GenCard(stack);
    }

    private UIElement GenTestItem(CardioSimulator.Core.Domain.Test test)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = test.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap });
        var minutes = test.QuestionTimeSeconds > 0 ? (int)Math.Round(test.QuestionTimeSeconds * test.Questions.Count / 60.0) : 0;
        info.Children.Add(new TextBlock
        {
            Text = minutes > 0 ? AppStrings.TestGenReadyMetaFormat(test.Questions.Count, minutes) : AppStrings.TestGenReadyUntimedFormat(test.Questions.Count),
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
        });
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var edit = new Button { Content = "✏️", Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(edit, AppStrings.BankEdit);
        var id = test.TestId;
        edit.Click += (_, _) => EditTestInEditor(id);
        var del = new Button { Content = "🗑️", Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(del, AppStrings.BankDelete);
        del.Click += async (_, _) => await OnGenDeleteTestAsync(id);
        actions.Children.Add(edit);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        return new Border
        {
            Child = row,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
        };
    }

    private async System.Threading.Tasks.Task OnGenDeleteTestAsync(string testId)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.TestCtorDelete,
            Content = AppStrings.TestCtorDeleteConfirm,
            PrimaryButtonText = AppStrings.TestCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // Clear the editor too if the deleted test happens to be the one open there.
        var ok = testId == _vm.TestId ? _vm.Delete() : _vm.Repository.DeleteTest(testId);
        if (ok) RenderGenerator();
    }

    private UIElement GenConstructorCard()
    {
        var stack = new StackPanel { Spacing = 12 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = AppStrings.TestGenCtorTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = AppStrings.TestGenStepHint, FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(title);

        stack.Children.Add(GenSteps());

        // Step 1 — test types.
        stack.Children.Add(new TextBlock { Text = AppStrings.TestGenPickTypes, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        stack.Children.Add(GenTypeGrid());

        // Step 2 — theme / rhythm.
        stack.Children.Add(new TextBlock { Text = AppStrings.TestGenPickTopic, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(GenSelectionRow());


        // Params.
        stack.Children.Add(GenParams());

        // Actions.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        var reset = new Button { Content = AppStrings.TestGenReset, HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) => { GenReset(); ShowToast("🔄", AppStrings.TestGenReset, AppStrings.TestGenSubtitle); };
        var generate = PrimaryButton(AppStrings.TestGenGenerate);
        generate.HorizontalAlignment = HorizontalAlignment.Stretch;
        generate.Click += (_, _) => GenGenerate(generate);
        var col = new Grid();
        col.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        col.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        col.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(reset, 0);
        Grid.SetColumn(generate, 2);
        col.Children.Add(reset);
        col.Children.Add(generate);
        stack.Children.Add(col);

        stack.Children.Add(GenHairline());
        stack.Children.Add(new TextBlock { Text = AppStrings.TestGenHint, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });

        return GenCard(stack);
    }

    private UIElement GenSteps()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(StepDot("1", AppStrings.TestGenStep1));
        row.Children.Add(new Border { Width = 30, Height = 2, Background = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(StepDot("2", AppStrings.TestGenStep2));
        return row;

        UIElement StepDot(string n, string label)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = AppTheme.Accent,
                Child = new TextBlock { Text = n, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            s.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
            return s;
        }
    }

    private UIElement GenTypeGrid()
    {
        var types = new (string Key, string Icon, string Label, string Desc)[]
        {
            ("questions", "📝", AppStrings.TestGenTypeQuestions, AppStrings.TestGenTypeQuestionsDesc),
            ("image", "🖼️", AppStrings.TestGenTypeImage, AppStrings.TestGenTypeImageDesc),
            ("detect", "🔍", AppStrings.TestGenTypeDetect, AppStrings.TestGenTypeDetectDesc),
            ("assemble", "✏️", AppStrings.TestGenTypeAssemble, AppStrings.TestGenTypeAssembleDesc),
            ("clinical", "🏥", AppStrings.TestGenTypeClinical, AppStrings.TestGenTypeClinicalDesc),
        };

        var grid = new Grid();
        for (var i = 0; i < types.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnSpacing = 8;

        for (var i = 0; i < types.Length; i++)
        {
            var t = types[i];
            var active = _genTypes.Contains(t.Key);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(new TextBlock { Text = t.Icon, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = t.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = t.Desc, FontSize = 10, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            // Bottom row: the check mark indicating selection state.
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            bottom.Children.Add(new TextBlock { Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent, Opacity = active ? 1 : 0, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(bottom);

            var btn = new Button
            {
                Content = content,
                // Stretch vertically so every tab in the row takes the tallest tab's height — equal
                // rectangles instead of each button sizing to its own (variable-length) description.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Background = active ? AppTheme.AppAccentSoftBackground : AppTheme.AppCardBackground,
                BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder,
                BorderThickness = new Thickness(active ? 2 : 1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(6, 10, 6, 10),
            };
            var key = t.Key;
            btn.Click += (_, _) =>
            {
                if (!_genTypes.Remove(key)) _genTypes.Add(key);
                if (_genTypes.Count == 0) _genTypes.Add(key); // keep at least one
                RenderGenerator();
            };
            Grid.SetColumn(btn, i);
            grid.Children.Add(btn);
        }
        return grid;
    }

    private UIElement GenSelectionRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnSpacing = 12;

        // ── Theme / section ──
        var themeGroup = new StackPanel { Spacing = 4 };
        themeGroup.Children.Add(new TextBlock { Text = AppStrings.TestGenTopicLabel, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });

        var themes = CourseSections();
        var themeBox = new ComboBox { PlaceholderText = AppStrings.TestGenTopicPlaceholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Keep every section/sub-topic in the list — already-picked ones are highlighted (✓ + accent) rather
        // than removed, so the list stays stable. Re-selecting a picked entry is a no-op (guarded below).
        foreach (var s in themes)
        {
            var picked = _genThemes.Contains(s.Value, StringComparer.CurrentCultureIgnoreCase);
            var item = new ComboBoxItem { Content = $"{(picked ? "✓ " : string.Empty)}{s.Display}", Tag = s.Value };
            if (picked)
            {
                item.Foreground = AppTheme.Accent;
                item.FontWeight = FontWeights.SemiBold;
            }
            themeBox.Items.Add(item);
        }
        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedItem is ComboBoxItem item && item.Tag is string th)
            {
                // Already picked → leave it highlighted, don't add a duplicate. Still re-render so the
                // combo resets to its placeholder (the picked item never sticks as the box's selection).
                if (!_genThemes.Contains(th, StringComparer.CurrentCultureIgnoreCase)) _genThemes.Add(th);
                RenderGenerator();
            }
        };
        themeGroup.Children.Add(themeBox);

        // The live "available to generate" count is a single combined badge below (see GenAvailableBadge),
        // since it now reflects types + themes + acronyms together — not a theme-only tally.
        var themeTags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var th in _genThemes)
        {
            var captured = th;
            themeTags.Children.Add(TagChip("📖 " + th, () => { _genThemes.Remove(captured); RenderGenerator(); }));
        }
        themeGroup.Children.Add(themeTags);
        Grid.SetColumn(themeGroup, 0);
        grid.Children.Add(themeGroup);

        // ── OR / AND toggle ──
        var toggleGroup = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var toggle = new Button
        {
            Content = _genOrMode ? "или" : "+",
            MinWidth = 46,
            CornerRadius = new CornerRadius(30),
            Background = _genOrMode ? AppTheme.AppSubtleFill : AppTheme.AppAccentSoftBackground,
            BorderBrush = _genOrMode ? AppTheme.AppCardBorder : AppTheme.Accent,
            BorderThickness = new Thickness(2),
            Foreground = _genOrMode ? AppTheme.TextSecondary : AppTheme.Accent,
            FontWeight = FontWeights.Bold,
        };
        toggle.Click += (_, _) =>
        {
            _genOrMode = !_genOrMode;
            ShowToast("🔄", AppStrings.TestGenModeLabel, _genOrMode ? AppStrings.TestGenModeOrToast : AppStrings.TestGenModeAndToast);
            RenderGenerator();
        };
        toggleGroup.Children.Add(toggle);
        toggleGroup.Children.Add(new TextBlock { Text = AppStrings.TestGenModeLabel, FontSize = 10, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetColumn(toggleGroup, 1);
        grid.Children.Add(toggleGroup);

        // ── Rhythm / pattern ──
        var rhythmGroup = new StackPanel { Spacing = 4 };
        rhythmGroup.Children.Add(new TextBlock { Text = AppStrings.TestGenRhythmLabel, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });

        var rhythmBox = new ComboBox { PlaceholderText = AppStrings.TestGenRhythmPlaceholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        // List the acronyms exhibited by loaded rhythms (the clinical join key), not the rhythms themselves.
        // Keep every acronym in the list — already-picked ones are highlighted (✓ + accent) rather than
        // removed. Re-selecting a picked entry is a no-op and never counts against the limit (see below).
        foreach (var code in RhythmAcronyms().OrderBy(AcronymLabel, StringComparer.CurrentCultureIgnoreCase))
        {
            var picked = _genAcronyms.Contains(code, StringComparer.OrdinalIgnoreCase);
            var item = new ComboBoxItem { Content = $"{(picked ? "✓ " : string.Empty)}{AcronymLabel(code)}", Tag = code };
            if (picked)
            {
                item.Foreground = AppTheme.Accent;
                item.FontWeight = FontWeights.SemiBold;
            }
            rhythmBox.Items.Add(item);
        }
        rhythmBox.SelectionChanged += (_, _) =>
        {
            if (rhythmBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
            {
                // Already picked → highlighted, not re-added; must not trip the limit toast. Re-render so
                // the combo resets to its placeholder instead of sticking on the picked item.
                if (_genAcronyms.Contains(code, StringComparer.OrdinalIgnoreCase)) { RenderGenerator(); return; }
                if (_genAcronyms.Count >= MaxGenAcronyms)
                {
                    ShowToast("⚠️", AppStrings.TestGenLimitFormat(MaxGenAcronyms), string.Empty);
                    return;
                }
                _genAcronyms.Add(code);
                RenderGenerator();
            }
        };
        rhythmGroup.Children.Add(rhythmBox);

        var rhythmTags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var code in _genAcronyms)
        {
            var captured = code;
            rhythmTags.Children.Add(TagChip("💓 " + AcronymLabel(code), () => { _genAcronyms.Remove(captured); RenderGenerator(); }));
        }
        rhythmGroup.Children.Add(rhythmTags);
        Grid.SetColumn(rhythmGroup, 2);
        grid.Children.Add(rhythmGroup);

        return grid;
    }

    private UIElement GenParams()
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var count = new StackPanel { Spacing = 4 };
        count.Children.Add(new TextBlock { Text = AppStrings.TestGenCount, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        var countBox = new NumberBox { Value = _genCount, Minimum = 1, Maximum = 100, SmallChange = 1, LargeChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };
        countBox.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) _genCount = Math.Clamp((int)e.NewValue, 1, 100); };
        count.Children.Add(countBox);
        Grid.SetColumn(count, 0);
        grid.Children.Add(count);

        var time = new StackPanel { Spacing = 4 };
        time.Children.Add(new TextBlock { Text = AppStrings.TestGenTime, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        var timeBox = new NumberBox { Value = _genTime, Minimum = 1, Maximum = 240, SmallChange = 1, LargeChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };
        timeBox.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) _genTime = Math.Clamp((int)e.NewValue, 1, 240); };
        time.Children.Add(timeBox);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        return grid;
    }

    private void GenReset()
    {
        _genTypes.Clear();
        _genTypes.Add("questions");
        _genThemes.Clear();
        _genAcronyms.Clear();
        _genOrMode = true;
        _genCount = 10;
        _genTime = 15;
        RenderGenerator();
    }

    /// <summary>Whether a bank question belongs to a generator test-type key ("questions" | "image" |
    /// "detect" | "assemble" | "clinical"). The keys overlap by design: a Text question is both "questions"
    /// and "clinical"; an ECG question is both "questions" and "detect".</summary>
    private static bool QuestionIsType(CardioSimulator.Core.Domain.TestQuestion q, string key) => key switch
    {
        "questions" => !q.IsAssembly && q.Stimulus is QuestionStimulus.Text or QuestionStimulus.Ecg,
        "image" => !q.IsAssembly && q.Stimulus == QuestionStimulus.Image,
        "detect" => !q.IsAssembly && q.Stimulus == QuestionStimulus.Ecg,
        "assemble" => q.IsAssembly,
        "clinical" => !q.IsAssembly && q.Stimulus == QuestionStimulus.Text,
        _ => false,
    };

    /// <summary>
    /// A predicate matching bank questions against the current theme/acronym selection under the OR/AND
    /// mode, or null when no topic is selected. Shared by <see cref="GenCandidates"/> and the availability
    /// counts (per-type + combined) so they can never diverge from what Generate would pull.
    /// </summary>
    private Func<CardioSimulator.Core.Domain.TestQuestion, bool>? GenTopicPredicate()
    {
        var hasThemes = _genThemes.Count > 0;
        var hasAcronyms = _genAcronyms.Count > 0;
        if (!hasThemes && !hasAcronyms) return null;

        // Normalize the picked acronyms and expand them to the rhythm ids that exhibit them, so a question
        // matches either by its bound rhythm (PathologyId) or by a direct acronym tag on the question.
        var selectedAcr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _genAcronyms)
            if (Taxonomy.Normalize(a) is { } n) selectedAcr.Add(n);
        var expandedRhythmIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _rhythmVm.Rhythms)
            if (r.AcronymList.Any(a => Taxonomy.Normalize(a) is { } n && selectedAcr.Contains(n)))
                expandedRhythmIds.Add(r.Id);

        bool InThemes(CardioSimulator.Core.Domain.TestQuestion q) =>
            q.Theme is { } t && _genThemes.Contains(t, StringComparer.CurrentCultureIgnoreCase);
        bool InAcronyms(CardioSimulator.Core.Domain.TestQuestion q) =>
            (q.PathologyId is { } p && expandedRhythmIds.Contains(p)) ||
            q.AcronymList.Any(a => Taxonomy.Normalize(a) is { } n && selectedAcr.Contains(n));

        return q => hasThemes && hasAcronyms
            ? (_genOrMode ? InThemes(q) || InAcronyms(q) : InThemes(q) && InAcronyms(q))
            : hasThemes ? InThemes(q) : InAcronyms(q);
    }

    /// <summary>
    /// The bank questions the current generator selection would draw from: those matching the picked test
    /// types AND the picked themes/acronyms under the current OR/AND mode. This is the exact pool
    /// <see cref="GenGenerate"/> shuffles and samples, so the availability badges and the Generate button can
    /// never disagree. Returns empty when no type — or no topic (theme/acronym) — is selected.
    /// </summary>
    private List<CardioSimulator.Core.Domain.TestQuestion> GenCandidates()
    {
        if (_genTypes.Count == 0 || GenTopicPredicate() is not { } topic)
            return new List<CardioSimulator.Core.Domain.TestQuestion>();
        return _vm.Bank.Questions.Where(q => _genTypes.Any(t => QuestionIsType(q, t)) && topic(q)).ToList();
    }



    private void GenGenerate(Button generateBtn)
    {
        if (_genTypes.Count == 0) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrNoType); return; }
        if (_genThemes.Count == 0 && _genAcronyms.Count == 0) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrNoTopic); return; }

        var rng = new Random();

        // Bank pool: matching questions, shuffled (Fisher–Yates), capped at the requested count.
        var candidates = GenCandidates();
        Shuffle(candidates, rng);
        var chosen = candidates.Take(_genCount).ToList();

        // On-the-fly synthesis for the ECG-based types («Определи ЭКГ» / «Собери ЭКГ»): when the bank does
        // not cover the requested count for the selected acronyms — including a freshly-tagged rhythm/pattern
        // with zero bank questions — build the shortfall from the loaded rhythms. These questions live only in
        // this generated test; they are never written back to the bank. When both types are picked the
        // shortfall is split evenly between them.
        if (chosen.Count < _genCount && _genAcronyms.Count > 0)
        {
            var kinds = new List<string>();
            if (_genTypes.Contains("detect")) kinds.Add("detect");
            if (_genTypes.Contains("assemble")) kinds.Add("assemble");
            var need = _genCount - chosen.Count;
            for (var k = 0; k < kinds.Count && chosen.Count < _genCount; k++)
            {
                var remaining = _genCount - chosen.Count;
                // Last kind takes whatever is left; earlier ones take an even (ceiling) share.
                var share = k == kinds.Count - 1
                    ? remaining
                    : Math.Min(remaining, (int)Math.Ceiling(need / (double)kinds.Count));
                chosen.AddRange(kinds[k] == "detect"
                    ? SynthesizeDetectQuestions(_genAcronyms, share, rng)
                    : SynthesizeAssembleQuestions(_genAcronyms, share, rng));
            }
        }

        if (chosen.Count == 0) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrEmpty); return; }

        // Mix bank + synthesized questions so the on-the-fly ones aren't clustered at the tail.
        Shuffle(chosen, rng);

        var perQuestion = (int)Math.Round(_genTime * 60.0 / chosen.Count);
        var questions = chosen
            .Select((q, i) => q with { Id = TestConstructorViewModel.NewId(), Number = i + 1 })
            .ToList();

        var titleParts = _genThemes.Concat(_genAcronyms).Take(3).ToList();
        var titleBody = titleParts.Count > 0 ? string.Join(" · ", titleParts) : AppStrings.TestGenDefaultTitleFormat(TestConstructorViewModel.NewId());
        if (titleBody.Length > 70) titleBody = titleBody[..70].TrimEnd() + "…";

        var test = new CardioSimulator.Core.Domain.Test(TestConstructorViewModel.NewId(), titleBody, questions, perQuestion);
        if (!_vm.Repository.WriteTest(test))
        {
            ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrEmpty);
            return;
        }

        // Flash the button for immediate feedback, then open a modal quick-preview of what was generated.
        // The preview re-renders the generator (resetting the button) when it closes.
        generateBtn.Content = AppStrings.TestGenGenerateDone;
        _ = ShowGeneratedTestPreviewAsync(test);
    }

    /// <summary>
    /// Modal quick-preview of a freshly generated test: a scrollable list of its questions (type chip,
    /// text, correct answer or assembly parts, bound rhythm), with an "Open in editor" action. Closing it
    /// (or dismissing) refreshes the generator view; opening in the editor loads the saved test.
    /// </summary>
    private async Task ShowGeneratedTestPreviewAsync(CardioSimulator.Core.Domain.Test test)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"✅ {AppStrings.TestGenCreatedTitle}",
                Content = BuildGeneratedTestPreview(test),
                PrimaryButtonText = AppStrings.TestGenPreviewOpen,
                CloseButtonText = AppStrings.CommonClose,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = AppTheme.Current,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                EditTestInEditor(test.TestId);
                return;
            }
        }
        catch
        {
            // If the dialog can't be shown for any reason, fall through and just refresh the generator.
        }
        RenderGenerator();
    }

    /// <summary>Builds the scrollable body of the post-generation preview dialog — a meta subtitle plus one
    /// compact card per question.</summary>
    private UIElement BuildGeneratedTestPreview(CardioSimulator.Core.Domain.Test test)
    {
        var root = new StackPanel { Spacing = 10, MinWidth = 440 };

        var totalMinutes = test.QuestionTimeSeconds > 0
            ? (int)Math.Round(test.QuestionTimeSeconds * test.Questions.Count / 60.0)
            : 0;
        root.Children.Add(new TextBlock
        {
            Text = AppStrings.TestGenCreatedDescFormat(test.Questions.Count, totalMinutes),
            FontSize = 13,
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 8 };
        var n = 1;
        foreach (var q in test.Questions)
            list.Children.Add(PreviewQuestionRow(n++, q));

        root.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
        });
        return root;
    }

    /// <summary>One compact preview card for a generated question: number + type chip, the prompt, and a
    /// detail line (correct answer + bound rhythm for single-choice; part count + source rhythm for assembly).</summary>
    private UIElement PreviewQuestionRow(int number, CardioSimulator.Core.Domain.TestQuestion q)
    {
        var stack = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = "#" + number, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(q.IsAssembly ? ChipLabel(AppStrings.TestCtorStimulusAssemble) : StimulusChip(q.Stimulus));
        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(q.Text))
            stack.Children.Add(new TextBlock { Text = q.Text, FontSize = 13, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap });

        if (q.IsAssembly)
        {
            var detail = AppStrings.TestGenPreviewPartsFormat(q.Assemble?.PartCount ?? 0);
            if (q.Assemble?.SourcePathologyId is { } sid)
                detail += "   ·   💓 " + EcgLabel(sid);
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            if (q.CorrectOption() is { } opt && !string.IsNullOrWhiteSpace(opt.Text))
                stack.Children.Add(new TextBlock { Text = AppStrings.TestGenPreviewAnswerFormat(opt.Text), FontSize = 12, Foreground = AppTheme.Accent, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (q.PathologyId is { } pid)
                stack.Children.Add(new TextBlock { Text = "💓 " + EcgLabel(pid), FontSize = 12, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap });
        }

        return new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Child = stack,
        };
    }

    /// <summary>In-place Fisher–Yates shuffle.</summary>
    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>The localized taxonomy display name for an acronym (RU name in RU, else English), or null
    /// when the acronym is unknown or unnamed. This is the human-readable diagnosis label used for the
    /// synthesized «Определи ЭКГ» options.</summary>
    private string? TaxonomyName(string code)
    {
        var entry = Taxonomy.Shared.Find(code);
        var name = (_appVm.SelectedLanguage == DomainLanguage.RU ? entry?.NameRu : entry?.NameEn)?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }



    private HashSet<string> SelectedAcronymSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _genAcronyms)
            if (Taxonomy.Normalize(a) is { } n) set.Add(n);
        return set;
    }

    /// <summary>
    /// Synthesizes up to <paramref name="count"/> «Определи ЭКГ» (detect) questions for the selected
    /// acronyms without touching the question bank. Each shows a real rhythm trace (a loaded pathology that
    /// exhibits the acronym) and asks the student to name the pattern; options are the correct taxonomy name
    /// plus plausible distractors drawn from other acronyms (same rhythm-group first). Returns an empty list
    /// when no loaded rhythm exhibits any selected acronym.
    /// </summary>
    private List<CardioSimulator.Core.Domain.TestQuestion> SynthesizeDetectQuestions(
        IReadOnlyList<string> acronyms, int count, Random rng)
    {
        var result = new List<CardioSimulator.Core.Domain.TestQuestion>();
        if (count <= 0) return result;

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in acronyms)
            if (Taxonomy.Normalize(a) is { } n) selected.Add(n);
        if (selected.Count == 0) return result;

        // (rhythm, acronym) targets — every loaded rhythm that exhibits a selected acronym. The rhythm is
        // the trace to show; the acronym is the pattern to identify (its taxonomy name is the answer).
        var targets = new List<(PathologyEntry Rhythm, string Acronym)>();
        foreach (var r in _rhythmVm.Rhythms)
            foreach (var a in r.AcronymList)
                if (Taxonomy.Normalize(a) is { } n && selected.Contains(n))
                    targets.Add((r, n));
        if (targets.Count == 0) return result;
        Shuffle(targets, rng);

        for (var i = 0; i < count; i++)
        {
            var (rhythm, code) = targets[i % targets.Count]; // cycle when more questions than targets
            var correctName = TaxonomyName(code) ?? EcgLabel(rhythm);
            var distractors = DetectDistractorNames(code, correctName, rng);

            // Correct answer + distractors, order-shuffled; ids a, b, c, …
            var texts = new List<string> { correctName };
            texts.AddRange(distractors);
            Shuffle(texts, rng);
            var options = texts
                .Select((t, n) => new CardioSimulator.Core.Domain.TestOption(((char)('a' + n)).ToString(), t))
                .ToList();
            var correctId = options
                .First(o => string.Equals(o.Text, correctName, StringComparison.CurrentCultureIgnoreCase)).Id;

            result.Add(new CardioSimulator.Core.Domain.TestQuestion(
                Id: TestConstructorViewModel.NewId(),
                Number: 0, // renumbered by the caller
                Text: AppStrings.TestGenDetectPrompt,
                Options: options,
                CorrectOptionId: correctId,
                Comment: AppStrings.TestGenDetectComment(correctName),
                PathologyId: rhythm.Id,
                Acronyms: new[] { code }));
        }
        return result;
    }

    /// <summary>
    /// Synthesizes up to <paramref name="count"/> «Собери ЭКГ» (assemble) questions for the selected acronyms
    /// without touching the question bank. Each cuts a real rhythm's lead (a loaded pathology that exhibits
    /// the acronym) into parts for the student to reorder — the same slicing the editor uses. To vary repeats
    /// drawn from the same rhythm, the part count cycles 3→6. Rhythms too short to slice are skipped; returns
    /// an empty list when none can be sliced.
    /// </summary>
    private List<CardioSimulator.Core.Domain.TestQuestion> SynthesizeAssembleQuestions(
        IReadOnlyList<string> acronyms, int count, Random rng)
    {
        var result = new List<CardioSimulator.Core.Domain.TestQuestion>();
        if (count <= 0) return result;

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in acronyms)
            if (Taxonomy.Normalize(a) is { } n) selected.Add(n);
        if (selected.Count == 0) return result;

        // Distinct target rhythms exhibiting a selected acronym (each keeps its matched acronym for tagging).
        // Unlike detect, the acronym identity doesn't affect the task — ordering fragments — only relevance.
        var targets = new List<(PathologyEntry Rhythm, string Acronym)>();
        foreach (var r in _rhythmVm.Rhythms)
            foreach (var a in r.AcronymList)
                if (Taxonomy.Normalize(a) is { } n && selected.Contains(n)) { targets.Add((r, n)); break; }
        if (targets.Count == 0) return result;
        Shuffle(targets, rng);

        var fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
        // Cap attempts so an all-un-sliceable selection can't spin: one pass per target beyond the target count.
        var maxAttempts = count + targets.Count;
        for (var attempt = 0; result.Count < count && attempt < maxAttempts; attempt++)
        {
            var (rhythm, code) = targets[attempt % targets.Count];
            var parts = 4 + (result.Count % 2); // 4→5, so repeats from one rhythm slice differently
            var assembly = EcgAssemblyBuilder.Build(_appVm.Repository, rhythm.Id, Lead.II, parts, fs);
            if (assembly is not { IsComplete: true }) continue;

            result.Add(new CardioSimulator.Core.Domain.TestQuestion(
                Id: TestConstructorViewModel.NewId(),
                Number: 0, // renumbered by the caller
                Text: AppStrings.AssembleTitle,
                Options: System.Array.Empty<CardioSimulator.Core.Domain.TestOption>(),
                CorrectOptionId: string.Empty,
                Comment: string.Empty,
                Assemble: assembly,
                Acronyms: new[] { code }));
        }
        return result;
    }

    /// <summary>Up to three plausible wrong-answer names for a synthesized detect question: the taxonomy
    /// names of other acronyms exhibited by the loaded rhythms, preferring the same rhythm-group as the
    /// correct acronym, de-duplicated and excluding the correct name.</summary>
    private List<string> DetectDistractorNames(string correctCode, string correctName, Random rng)
    {
        var group = Taxonomy.Shared.Find(correctCode)?.Group;
        string? GroupOf(string c) => Taxonomy.Shared.Find(c)?.Group;
        bool SameGroup(string c) =>
            !string.IsNullOrEmpty(group) && string.Equals(GroupOf(c), group, StringComparison.OrdinalIgnoreCase);

        var codes = RhythmAcronyms()
            .Where(c => !string.Equals(c, correctCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sameGroup = codes.Where(SameGroup).ToList();
        var other = codes.Where(c => !SameGroup(c)).ToList();
        Shuffle(sameGroup, rng);
        Shuffle(other, rng);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { correctName };
        foreach (var c in sameGroup.Concat(other))
        {
            if (TaxonomyName(c) is not { } nm || !seen.Add(nm)) continue;
            names.Add(nm);
            if (names.Count >= 3) break;
        }
        return names;
    }

    private UIElement GenBankStats()
    {
        var stack = new StackPanel { Spacing = 12 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = AppStrings.TestGenOpenBank, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = AppStrings.TestGenBankSubtitle, FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(title);

        var stats = new Grid();
        for (var i = 0; i < 4; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnSpacing = 12;
        AddStat(stats, 0, _vm.Bank.Questions.Count.ToString("N0"), AppStrings.TestGenStatQuestions);
        AddStat(stats, 1, _vm.Repository.Tests.Count.ToString("N0"), AppStrings.TestGenStatTests);
        AddStat(stats, 2, _rhythmVm.Rhythms.Count.ToString("N0"), AppStrings.TestGenStatRhythms);
        AddStat(stats, 3, CourseSections().Count(s => !s.IsSub).ToString("N0"), AppStrings.TestGenStatThemes);
        stack.Children.Add(stats);

        return GenCard(stack);

        void AddStat(Grid host, int col, string number, string label)
        {
            var tile = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            tile.Children.Add(new TextBlock { Text = number, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center });
            tile.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var border = new Border
            {
                Child = tile,
                Background = AppTheme.AppCardBackground,
                BorderBrush = AppTheme.AppCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 10, 10, 10),
            };
            Grid.SetColumn(border, col);
            host.Children.Add(border);
        }
    }

    private UIElement GenFooter()
    {
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = new TextBlock { Text = AppStrings.TestGenFooterNote, FontSize = 12, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(a, 0);
        footer.Children.Add(a);
        var b = new TextBlock { Text = AppStrings.TestGenSaved, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Accent, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(b, 1);
        footer.Children.Add(b);
        return new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(4, 12, 4, 0),
            Child = footer,
        };
    }

    // ── Generator view helpers ────────────────────────────────────────────────

    private static Border GenCard(UIElement child) => new()
    {
        Child = child,
        Background = AppTheme.AppSubtleFill,
        BorderBrush = AppTheme.AppCardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(20, 16, 20, 16),
    };

    private static Border GenHairline() => new()
    {
        Height = 1,
        Background = AppTheme.AppCardBorder,
        Margin = new Thickness(0, 4, 0, 4),
    };

    private static Border CountBadge(int n) => new()
    {
        Background = AppTheme.Accent,
        CornerRadius = new CornerRadius(30),
        Padding = new Thickness(10, 1, 10, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) },
    };

    private Border TagChip(string text, Action onRemove)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
        var remove = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = AppTheme.TextSecondary,
        };
        remove.Click += (_, _) => onRemove();
        row.Children.Add(remove);
        return new Border
        {
            Child = row,
            Background = AppTheme.AppAccentSoftBackground,
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(10, 2, 4, 2),
        };
    }

    private static Button PrimaryButton(string content)
    {
        var btn = new Button { Content = content };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
            btn.Style = s;
        return btn;
    }

    // ── Toast ──────────────────────────────────────────────────────────────────

    private void ShowToast(string emoji, string title, string desc)
    {
        _toastTitle.Text = string.IsNullOrEmpty(title) ? emoji : $"{emoji} {title}";
        _toastTitle.Foreground = AppTheme.TextPrimary;
        _toastDesc.Text = desc;
        _toastDesc.Visibility = string.IsNullOrEmpty(desc) ? Visibility.Collapsed : Visibility.Visible;
        _toast.Visibility = Visibility.Visible;

        _toastTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(3.5);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick -= OnToastTick;
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
    }

    private void OnToastTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _toast.Visibility = Visibility.Collapsed;
        sender.Stop();
    }

    /// <summary>Minimal flow layout (the WinUI SDK has no WrapPanel): lays children left-to-right,
    /// wrapping to a new row when the next child would overflow the available width.</summary>
    private sealed class WrapPanel : Panel
    {
        public double HSpacing { get; set; } = 6;
        public double VSpacing { get; set; } = 6;

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
        {
            double x = 0, y = 0, rowH = 0, maxRowW = 0;
            var limit = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
            foreach (var child in Children)
            {
                child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var d = child.DesiredSize;
                if (x > 0 && x + d.Width > limit) { maxRowW = Math.Max(maxRowW, x - HSpacing); x = 0; y += rowH + VSpacing; rowH = 0; }
                x += d.Width + HSpacing;
                rowH = Math.Max(rowH, d.Height);
            }
            maxRowW = Math.Max(maxRowW, x - HSpacing);
            var width = double.IsInfinity(available.Width) ? Math.Max(0, maxRowW) : available.Width;
            return new Windows.Foundation.Size(width, y + rowH);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            double x = 0, y = 0, rowH = 0;
            foreach (var child in Children)
            {
                var d = child.DesiredSize;
                if (x > 0 && x + d.Width > finalSize.Width) { x = 0; y += rowH + VSpacing; rowH = 0; }
                child.Arrange(new Windows.Foundation.Rect(x, y, d.Width, d.Height));
                x += d.Width + HSpacing;
                rowH = Math.Max(rowH, d.Height);
            }
            return finalSize;
        }
    }
}
