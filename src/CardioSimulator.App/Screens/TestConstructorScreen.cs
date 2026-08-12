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

    /// <summary>Max rhythms addable to a generation filter (mirrors the prototype's cap).</summary>
    private const int MaxGenRhythms = 6;

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
    private readonly ScrollViewer _generatorScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16, 12, 16, 16),
        Visibility = Visibility.Collapsed,
    };

    // ── Generator (landing view) state ────────────────────────────────────────
    // Selected test-type keys ("questions" | "image" | "detect" | "assemble" | "clinical").
    private readonly HashSet<string> _genTypes = new() { "questions" };
    private readonly List<string> _genThemes = new();
    private readonly List<string> _genRhythms = new();
    private bool _genOrMode = true;
    private int _genCount = 10;
    private int _genTime = 15;
    private bool _genWelcomed;

    // Toast overlay (bottom-right).
    private Border _toast = null!;
    private readonly TextBlock _toastTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _toastDesc = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;
    private StackPanel _testToolbar = null!;
    private StackPanel _bankToolbar = null!;
    private ToggleButton _startStop = null!;

    private ComboBox _testsBox = null!;
    private TextBox _titleBox = null!;
    private TextBox _timeBox = null!;
    private Button _saveBtn = null!;
    private Button _deleteBtn = null!;
    private TextBlock _status = null!;

    private readonly ScrollViewer _editorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ScrollViewer _bankScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    // Full-width host for the redesigned bank browse (shown instead of the monitor+editor split while
    // browsing the bank; the split returns when a question is opened for editing).
    private readonly ScrollViewer _bankBrowseScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16, 12, 16, 16),
        Visibility = Visibility.Collapsed,
    };
    private TextBlock _intro = null!;
    private bool _suppressTests;

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
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            _rhythmVm.PropertyChanged -= OnRhythmChanged;
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

        toolbar.Children.Add(BuildTestToolbar());
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

        // Row 1 hosts both the editor/bank body (monitor + editor) and the full-width generator
        // landing view; only one is visible at a time (ShowView). The monitor is never re-parented —
        // its column just goes Collapsed with the body while the generator is up.
        var row1Host = new Grid();
        row1Host.Children.Add(body);
        row1Host.Children.Add(_generatorScroll);
        row1Host.Children.Add(_bankBrowseScroll);
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

    private StackPanel BuildTestToolbar()
    {
        _testToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        _testsBox = new ComboBox { MinWidth = 200, PlaceholderText = AppStrings.TestCtorTestsLabel, VerticalAlignment = VerticalAlignment.Center };
        var newBtn = new Button { Content = AppStrings.TestCtorNew };
        _deleteBtn = new Button { Content = AppStrings.TestCtorDelete, IsEnabled = false };
        _titleBox = MakeTextBox(AppStrings.TestCtorTitleLabel, 200);
        _timeBox = MakeTextBox(AppStrings.TestCtorTimeLabel, 120);
        _saveBtn = new Button { Content = new SymbolIcon(Symbol.Save), IsEnabled = false };
        ToolTipService.SetToolTip(_saveBtn, AppStrings.TestCtorSave);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };

        _testToolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTestsLabel, VerticalAlignment = VerticalAlignment.Center });
        _testToolbar.Children.Add(_testsBox);
        _testToolbar.Children.Add(newBtn);
        _testToolbar.Children.Add(_deleteBtn);
        _testToolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTitleLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        _testToolbar.Children.Add(_titleBox);
        _testToolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTimeLabel, VerticalAlignment = VerticalAlignment.Center });
        _testToolbar.Children.Add(_timeBox);
        _testToolbar.Children.Add(_saveBtn);
        _testToolbar.Children.Add(_status);

        PopulateTests();
        _testsBox.SelectionChanged += (_, _) =>
        {
            if (_suppressTests) return;
            if (_testsBox.SelectedItem is ComboBoxItem item && item.Tag is string id && _vm.Load(id))
            {
                _titleBox.Text = _vm.Title;
                _timeBox.Text = _vm.QuestionTimeSeconds.ToString();
                _status.Text = string.Empty;
                RenderEditor();
            }
        };
        newBtn.Click += (_, _) =>
        {
            _vm.NewTest();
            _suppressTests = true;
            _testsBox.SelectedItem = null;
            _suppressTests = false;
            _titleBox.Text = _vm.Title;
            _timeBox.Text = _vm.QuestionTimeSeconds.ToString();
            _status.Text = string.Empty;
            RenderEditor();
        };
        _titleBox.TextChanged += (_, _) => { _vm.Title = _titleBox.Text; _vm.IsDirty = true; };
        _timeBox.TextChanged += (_, _) =>
        {
            _vm.QuestionTimeSeconds = int.TryParse(_timeBox.Text, out var s) && s >= 0 ? s : 0;
            _vm.IsDirty = true;
        };
        _saveBtn.Click += async (_, _) =>
        {
            if (!await EnsureAssembliesBuiltAsync(_vm.Questions)) return;
            if (_vm.Save())
            {
                _status.Text = AppStrings.TestCtorSaved;
                PopulateTests();
                RenderEditor();
            }
        };
        _deleteBtn.Click += async (_, _) => await OnDeleteTestAsync();

        return _testToolbar;
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
        var themesBtn = new Button { Content = AppStrings.TestCtorManageThemes };
        themesBtn.Click += async (_, _) => await OnManageThemesAsync();

        _bankToolbar.Children.Add(newBtn);
        _bankToolbar.Children.Add(importBtn);
        _bankToolbar.Children.Add(exportBtn);
        _bankToolbar.Children.Add(themesBtn);
        return _bankToolbar;
    }

    private void ShowView(View view)
    {
        _view = view;
        _generatorViewBtn.FontWeight = view == View.Generator ? FontWeights.Bold : FontWeights.Normal;
        _testsViewBtn.FontWeight = view == View.Tests ? FontWeights.Bold : FontWeights.Normal;
        _bankViewBtn.FontWeight = view == View.Bank ? FontWeights.Bold : FontWeights.Normal;
        _testToolbar.Visibility = view == View.Tests ? Visibility.Visible : Visibility.Collapsed;
        // The bank's actions (New / Import / Export / Themes) now live in the redesigned browse panel,
        // so the top-bar bank toolbar stays hidden.
        _bankToolbar.Visibility = Visibility.Collapsed;
        RenderActiveView();
    }

    private void RenderActiveView()
    {
        if (_view == View.Generator) RenderGenerator();
        else if (_view == View.Tests) RenderEditor();
        else RenderBank();
    }

    /// <summary>Toggles the three row-1 hosts (monitor+editor split / full-width generator / full-width
    /// bank browse) for the active view. The bank shows the full-width browse while listing and the
    /// monitor+editor split while a question is open for editing.</summary>
    private void UpdateHostVisibility()
    {
        var bankEditing = _view == View.Bank && _vm.BankEdit is not null;
        var showBody = _view == View.Tests || bankEditing;
        _body.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        _generatorScroll.Visibility = _view == View.Generator ? Visibility.Visible : Visibility.Collapsed;
        _bankBrowseScroll.Visibility = _view == View.Bank && !bankEditing ? Visibility.Visible : Visibility.Collapsed;
        if (!showBody) SetPreviewRunning(false); // park the monitor while a full-width view is up
    }

    // ── Monitor preview ───────────────────────────────────────────────────────

    private void RunPreview(string pathologyId)
    {
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

    private void PopulateTests()
    {
        _suppressTests = true;
        try
        {
            _testsBox.Items.Clear();
            foreach (var t in _vm.Repository.Tests)
                _testsBox.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.TestId });
            if (_vm.TestId is { } id)
                _testsBox.SelectedItem = _testsBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
        }
        finally
        {
            _suppressTests = false;
        }
    }

    private async Task OnDeleteTestAsync()
    {
        if (!_vm.HasTest) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.TestCtorDelete,
            Content = AppStrings.TestCtorDeleteConfirm,
            PrimaryButtonText = AppStrings.TestCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_vm.Delete())
        {
            _titleBox.Text = string.Empty;
            _timeBox.Text = string.Empty;
            _status.Text = string.Empty;
            PopulateTests();
            RenderEditor();
        }
    }

    private void RenderEditor()
    {
        UpdateHostVisibility();
        _bankScroll.Visibility = Visibility.Collapsed;

        var has = _vm.HasTest;
        _intro.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _editorScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _saveBtn.IsEnabled = has;
        _deleteBtn.IsEnabled = has;

        if (!has)
        {
            _editorScroll.Content = null;
            SetPreviewRunning(false);
            return;
        }

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(12, 8, 12, 8) };
        for (var i = 0; i < _vm.Questions.Count; i++)
            panel.Children.Add(BuildQuestionCard(_vm.Questions[i], i + 1, RenderEditor, inTest: true));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var add = new Button { Content = AppStrings.TestCtorAddQuestion };
        add.Click += (_, _) => { _vm.AddQuestion(); RenderEditor(); };
        buttons.Children.Add(add);
        var addFromBank = new Button { Content = AppStrings.TestCtorAddFromBank };
        addFromBank.Click += async (_, _) => await OnAddFromBankAsync();
        buttons.Children.Add(addFromBank);
        panel.Children.Add(buttons);

        _editorScroll.Content = panel;

        // Preview the first question's bound ECG (or an assembly's correct rhythm), if any.
        var firstEcg = _vm.Questions.FirstOrDefault(q => !q.IsAssembly && q.Kind == QuestionStimulus.Ecg && !string.IsNullOrWhiteSpace(q.PathologyId));
        var firstAssembly = _vm.Questions.FirstOrDefault(q => q.IsAssembly && !string.IsNullOrWhiteSpace(q.AssembleSourceId));
        if (firstEcg is not null) RunPreview(firstEcg.PathologyId!);
        else if (firstAssembly is not null) RunPreview(firstAssembly.AssembleSourceId!);
        else SetPreviewRunning(false);
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
        _bankBrowseScroll.Content = BuildBankList();
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
        var page = new StackPanel { Spacing = 16, MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(BankHeader());
        page.Children.Add(BankFilters());

        _bankItemsHost = new StackPanel { Spacing = 12 };
        page.Children.Add(_bankItemsHost);

        _bankPaginationHost = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(4, 12, 4, 0),
            Child = _bankPaginationHost,
        });

        RefreshBankItems();
        return page;
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
        stats.Children.Add(BankStat(_vm.Themes.Read().Count.ToString("N0"), AppStrings.Bank2StatThemes));
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
        var themes = new Button { Content = AppStrings.TestCtorManageThemes };
        themes.Click += async (_, _) => await OnManageThemesAsync();
        var create = PrimaryButton(AppStrings.BankNewQuestion);
        create.Click += (_, _) => { _vm.NewBankQuestion(); RenderBank(); };
        actions.Children.Add(import);
        actions.Children.Add(export);
        actions.Children.Add(themes);
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
        foreach (var theme in _vm.Themes.Read())
            themeCombo.Items.Add(new ComboBoxItem { Content = theme, Tag = theme });
        themeCombo.SelectedItem = themeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == _bankThemeFilter) ?? themeCombo.Items[0];
        themeCombo.SelectionChanged += (_, _) => { _bankThemeFilter = (themeCombo.SelectedItem as ComboBoxItem)?.Tag as string; _bankPage = 0; RefreshBankItems(); };
        themeGroup.Children.Add(themeCombo);
        Grid.SetColumn(themeGroup, 0);
        mainRow.Children.Add(themeGroup);

        var rhythmGroup = new StackPanel { Spacing = 4 };
        rhythmGroup.Children.Add(new TextBlock { Text = AppStrings.TestGenRhythmLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        var rhythmCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        rhythmCombo.Items.Add(new ComboBoxItem { Content = AppStrings.Bank2AllRhythms, Tag = null });
        foreach (var entry in _rhythmVm.Rhythms.OrderBy(EcgLabel, StringComparer.CurrentCultureIgnoreCase))
            rhythmCombo.Items.Add(new ComboBoxItem { Content = $"{entry.Id} — {EcgLabel(entry)}", Tag = entry.Id });
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
            q = q.Where(x => string.Equals(x.PathologyId, _bankRhythmFilter, StringComparison.Ordinal));
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
                if (_vm.SaveQuestionToBank(q)) _status.Text = AppStrings.TestCtorSavedToBank;
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
        foreach (var theme in _appVm.Themes.Read())
            themeCombo.Items.Add(new ComboBoxItem { Content = theme, Tag = theme });
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
        outer.Children.Add(BuildAcronymPicker(q));
        return outer;
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

        // Row 2: number of parts.
        var partsCombo = new ComboBox { MinWidth = 70, HorizontalAlignment = HorizontalAlignment.Left };
        for (var n = 3; n <= 6; n++)
            partsCombo.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        partsCombo.SelectedItem = partsCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (int)i.Tag == q.AssemblePartCount) ?? partsCombo.Items[1];
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
        _appVm.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;

    /// <summary>Localized display name by id. Prefer the <see cref="PathologyEntry"/> overload inside a
    /// loop over <see cref="RhythmViewModel.Rhythms"/> — this one scans the list, so calling it per item
    /// is O(n²) (that quadratic froze the assembly editor's two full-catalog combos on large datasets).</summary>
    private string EcgLabel(string id)
    {
        var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == id);
        return entry is null ? id : EcgLabel(entry);
    }

    /// <summary>Localized display name of a Course-Constructor course, used to seed the theme catalog.</summary>
    private string CourseThemeName(CourseEntry course) =>
        _appVm.SelectedLanguage == DomainLanguage.RU ? (course.NameRu ?? course.TitleEn) : course.TitleEn;

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

    // ── Theme management ──────────────────────────────────────────────────────

    private async Task OnManageThemesAsync()
    {
        var listHost = new StackPanel { Spacing = 4 };
        var addBox = new TextBox { PlaceholderText = AppStrings.ThemeAddPlaceholder, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };

        // Courses authored in the Course Constructor double as ready-made themes: offer the ones not
        // yet in the catalog in a picker so the user can pull them in with one click.
        var courseCombo = new ComboBox { MinWidth = 220, PlaceholderText = AppStrings.ThemeFromCourseHint, VerticalAlignment = VerticalAlignment.Center };

        void RebuildCourses()
        {
            var existing = new HashSet<string>(_appVm.Themes.Read(), StringComparer.CurrentCultureIgnoreCase);
            courseCombo.Items.Clear();
            foreach (var course in _appVm.CourseRepository.Courses)
            {
                var name = CourseThemeName(course);
                if (string.IsNullOrWhiteSpace(name) || existing.Contains(name)) continue;
                courseCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
            courseCombo.SelectedItem = null;
        }

        void Rebuild()
        {
            listHost.Children.Clear();
            foreach (var theme in _appVm.Themes.Read())
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = theme, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                var del = new Button { Content = "✕", MinWidth = 36 };
                var captured = theme;
                del.Click += (_, _) => { _appVm.Themes.Remove(captured); Rebuild(); };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                listHost.Children.Add(row);
            }
            RebuildCourses();
        }
        Rebuild();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addBox.MinWidth = 220;
        var addBtn = new Button { Content = AppStrings.ThemeAdd };
        addBtn.Click += (_, _) =>
        {
            if (_appVm.Themes.Add(addBox.Text)) { addBox.Text = string.Empty; Rebuild(); }
        };
        addRow.Children.Add(addBox);
        addRow.Children.Add(addBtn);

        var coursesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addCourseBtn = new Button { Content = AppStrings.ThemeAdd };
        addCourseBtn.Click += (_, _) =>
        {
            if ((courseCombo.SelectedItem as ComboBoxItem)?.Tag is string name && _appVm.Themes.Add(name))
                Rebuild();
        };
        coursesRow.Children.Add(new TextBlock { Text = AppStrings.ThemeFromCourse, VerticalAlignment = VerticalAlignment.Center });
        coursesRow.Children.Add(courseCombo);
        coursesRow.Children.Add(addCourseBtn);

        var content = new StackPanel { Spacing = 10, MinWidth = 320 };
        content.Children.Add(new ScrollViewer { Content = listHost, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(addRow);
        if (_appVm.CourseRepository.Courses.Count > 0)
            content.Children.Add(coursesRow);

        var dialog = new ContentDialog
        {
            Title = AppStrings.TestCtorManageThemes,
            Content = content,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        RenderBank(); // theme list / chips may have changed
    }

    private async Task InfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
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
        ShowView(View.Tests);
        _suppressTests = true;
        _testsBox.SelectedItem = null;
        _suppressTests = false;
        _titleBox.Text = _vm.Title;
        _timeBox.Text = _vm.QuestionTimeSeconds.ToString();
        _status.Text = string.Empty;
        RenderEditor();
    }

    private void EditTestInEditor(string testId)
    {
        if (!_vm.Load(testId)) return;
        ShowView(View.Tests);
        _suppressTests = true;
        _testsBox.SelectedItem = _testsBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == testId);
        _suppressTests = false;
        _titleBox.Text = _vm.Title;
        _timeBox.Text = _vm.QuestionTimeSeconds.ToString();
        _status.Text = string.Empty;
        RenderEditor();
    }

    private void RenderGenerator()
    {
        UpdateHostVisibility();
        var page = new StackPanel { Spacing = 16, MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(GenHeader());
        page.Children.Add(GenMainGrid());
        page.Children.Add(GenBankStats());
        page.Children.Add(GenFooter());
        _generatorScroll.Content = page;
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

        var left = GenReadyTestsCard();
        Grid.SetColumn((FrameworkElement)left, 0);
        grid.Children.Add(left);

        var right = GenConstructorCard();
        Grid.SetColumn((FrameworkElement)right, 2);
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
        if (_vm.Repository.DeleteTest(testId))
        {
            PopulateTests();
            RenderGenerator();
        }
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
            if (active)
                content.Children.Add(new TextBlock { Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent, HorizontalAlignment = HorizontalAlignment.Center });

            var btn = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
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

        var themes = _vm.Themes.Read();
        var themeBox = new ComboBox { PlaceholderText = AppStrings.TestGenTopicPlaceholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var th in themes.Where(th => !_genThemes.Contains(th, StringComparer.CurrentCultureIgnoreCase)))
            themeBox.Items.Add(new ComboBoxItem { Content = th, Tag = th });
        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedItem is ComboBoxItem item && item.Tag is string th)
            {
                if (!_genThemes.Contains(th, StringComparer.CurrentCultureIgnoreCase)) _genThemes.Add(th);
                RenderGenerator();
            }
        };
        themeGroup.Children.Add(themeBox);

        // Count of bank questions under the selected themes.
        var themeCount = _genThemes.Count == 0 ? 0 : _vm.Bank.Questions.Count(q =>
            q.Theme is { } qt && _genThemes.Contains(qt, StringComparer.CurrentCultureIgnoreCase));
        if (_genThemes.Count > 0)
            themeGroup.Children.Add(new Border
            {
                Background = AppTheme.AppAccentSoftBackground,
                CornerRadius = new CornerRadius(30),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
                Child = new TextBlock { Text = AppStrings.TestGenTopicCountFormat(themeCount), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Accent },
            });

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
        foreach (var entry in _rhythmVm.Rhythms.Where(r => !_genRhythms.Contains(r.Id)).OrderBy(EcgLabel, StringComparer.CurrentCultureIgnoreCase))
            rhythmBox.Items.Add(new ComboBoxItem { Content = $"{entry.Id} — {EcgLabel(entry)}", Tag = entry.Id });
        rhythmBox.SelectionChanged += (_, _) =>
        {
            if (rhythmBox.SelectedItem is ComboBoxItem item && item.Tag is string rid)
            {
                if (_genRhythms.Count >= MaxGenRhythms)
                {
                    ShowToast("⚠️", AppStrings.TestGenLimitFormat(MaxGenRhythms), string.Empty);
                    return;
                }
                if (!_genRhythms.Contains(rid)) _genRhythms.Add(rid);
                RenderGenerator();
            }
        };
        rhythmGroup.Children.Add(rhythmBox);

        var rhythmTags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var rid in _genRhythms)
        {
            var captured = rid;
            var label = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == rid) is { } e ? $"{rid} — {EcgLabel(e)}" : rid;
            rhythmTags.Children.Add(TagChip("💓 " + label, () => { _genRhythms.Remove(captured); RenderGenerator(); }));
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
        _genRhythms.Clear();
        _genOrMode = true;
        _genCount = 10;
        _genTime = 15;
        RenderGenerator();
    }

    private void GenGenerate(Button generateBtn)
    {
        if (_genTypes.Count == 0) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrNoType); return; }

        var hasThemes = _genThemes.Count > 0;
        var hasRhythms = _genRhythms.Count > 0;
        if (!hasThemes && !hasRhythms) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrNoTopic); return; }

        bool TypeMatch(CardioSimulator.Core.Domain.TestQuestion q) =>
            (_genTypes.Contains("questions") && !q.IsAssembly && q.Stimulus is QuestionStimulus.Text or QuestionStimulus.Ecg) ||
            (_genTypes.Contains("image") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Image) ||
            (_genTypes.Contains("detect") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Ecg) ||
            (_genTypes.Contains("assemble") && q.IsAssembly) ||
            (_genTypes.Contains("clinical") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Text);

        bool InThemes(CardioSimulator.Core.Domain.TestQuestion q) =>
            q.Theme is { } t && _genThemes.Contains(t, StringComparer.CurrentCultureIgnoreCase);
        bool InRhythms(CardioSimulator.Core.Domain.TestQuestion q) =>
            q.PathologyId is { } p && _genRhythms.Contains(p);
        bool TopicMatch(CardioSimulator.Core.Domain.TestQuestion q) =>
            hasThemes && hasRhythms ? (_genOrMode ? InThemes(q) || InRhythms(q) : InThemes(q) && InRhythms(q))
            : hasThemes ? InThemes(q)
            : InRhythms(q);

        var candidates = _vm.Bank.Questions.Where(q => TypeMatch(q) && TopicMatch(q)).ToList();
        if (candidates.Count == 0) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrEmpty); return; }

        // Shuffle (Fisher–Yates) and take up to the requested count.
        var rng = new Random();
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        var chosen = candidates.Take(_genCount).ToList();

        var perQuestion = (int)Math.Round(_genTime * 60.0 / chosen.Count);
        var questions = chosen
            .Select((q, i) => q with { Id = TestConstructorViewModel.NewId(), Number = i + 1 })
            .ToList();

        var titleParts = _genThemes.Concat(_genRhythms).Take(3).ToList();
        var titleBody = titleParts.Count > 0 ? string.Join(" · ", titleParts) : AppStrings.TestGenDefaultTitleFormat(TestConstructorViewModel.NewId());
        if (titleBody.Length > 70) titleBody = titleBody[..70].TrimEnd() + "…";

        var test = new CardioSimulator.Core.Domain.Test(TestConstructorViewModel.NewId(), titleBody, questions, perQuestion);
        if (!_vm.Repository.WriteTest(test))
        {
            ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.TestGenErrEmpty);
            return;
        }

        PopulateTests();
        var totalMinutes = perQuestion > 0 ? (int)Math.Round(perQuestion * questions.Count / 60.0) : 0;
        ShowToast("✅", AppStrings.TestGenCreatedTitle, AppStrings.TestGenCreatedDescFormat(questions.Count, totalMinutes));

        // Flash the button, then revert once the toast timer would have cleared.
        generateBtn.Content = AppStrings.TestGenGenerateDone;
        var flip = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        flip.Interval = TimeSpan.FromSeconds(2.5);
        flip.IsRepeating = false;
        flip.Tick += (s, _) => { s.Stop(); RenderGenerator(); };
        flip.Start();
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
        AddStat(stats, 3, _vm.Themes.Read().Count.ToString("N0"), AppStrings.TestGenStatThemes);
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
