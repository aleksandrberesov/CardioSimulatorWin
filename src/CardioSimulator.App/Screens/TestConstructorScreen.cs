using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
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
    private enum View { Tests, Bank }

    private readonly TestConstructorViewModel _vm;
    private readonly MonitorViewModel _monitorVm;
    private readonly RhythmViewModel _rhythmVm;
    private readonly AppViewModel _appVm;
    private readonly MonitorView _monitor = new();
    private readonly Func<Task<StorageFile?>> _pickOpenImage;
    private readonly Func<Task<StorageFile?>> _pickOpenJson;
    private readonly Func<Task<StorageFile?>> _pickSaveJson;

    private View _view = View.Tests;

    private Button _testsViewBtn = null!;
    private Button _bankViewBtn = null!;
    private StackPanel _viewToggle = null!;
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
    private TextBlock _intro = null!;
    private bool _suppressTests;

    // Bank list filter state.
    private string? _bankThemeFilter;
    private string _bankSearch = string.Empty;

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
        Unloaded += (_, _) => _rhythmVm.PropertyChanged -= OnRhythmChanged;
        ShowView(View.Tests);
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

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        return root;
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
        _saveBtn.Click += (_, _) =>
        {
            if (_vm.Save())
            {
                _status.Text = AppStrings.TestCtorSaved;
                PopulateTests();
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
        _testsViewBtn.FontWeight = view == View.Tests ? FontWeights.Bold : FontWeights.Normal;
        _bankViewBtn.FontWeight = view == View.Bank ? FontWeights.Bold : FontWeights.Normal;
        _testToolbar.Visibility = view == View.Tests ? Visibility.Visible : Visibility.Collapsed;
        _bankToolbar.Visibility = view == View.Bank ? Visibility.Visible : Visibility.Collapsed;
        RenderActiveView();
    }

    private void RenderActiveView()
    {
        if (_view == View.Tests) RenderEditor();
        else RenderBank();
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

        // Preview the first question's bound ECG, if any.
        var firstEcg = _vm.Questions.FirstOrDefault(q => q.Kind == QuestionStimulus.Ecg && !string.IsNullOrWhiteSpace(q.PathologyId));
        if (firstEcg is not null) RunPreview(firstEcg.PathologyId!);
        else SetPreviewRunning(false);
    }

    // ── Bank view ─────────────────────────────────────────────────────────────

    private void RenderBank()
    {
        _intro.Visibility = Visibility.Collapsed;
        _editorScroll.Visibility = Visibility.Collapsed;
        _bankScroll.Visibility = Visibility.Visible;

        if (_vm.BankEdit is { } editing)
        {
            // Single-question card editor with Save / Cancel.
            var panel = new StackPanel { Spacing = 12, Padding = new Thickness(12, 8, 12, 8) };
            panel.Children.Add(BuildQuestionCard(editing, number: null, RenderBank, inTest: false));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            var save = new Button { Content = AppStrings.BankSave };
            save.Click += (_, _) =>
            {
                if (_vm.SaveBankQuestion()) RenderBank();
            };
            var cancel = new Button { Content = AppStrings.CommonCancel };
            cancel.Click += (_, _) => { _vm.CancelBankEdit(); RenderBank(); };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            _bankScroll.Content = panel;

            if (editing.Kind == QuestionStimulus.Ecg && !string.IsNullOrWhiteSpace(editing.PathologyId)) RunPreview(editing.PathologyId!);
            else SetPreviewRunning(false);
            return;
        }

        _bankScroll.Content = BuildBankList();
        SetPreviewRunning(false);
    }

    private UIElement BuildBankList()
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(12, 8, 12, 8) };

        // Filter row: theme combo + search box.
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var themeCombo = new ComboBox { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        themeCombo.Items.Add(new ComboBoxItem { Content = AppStrings.BankFilterAll, Tag = null });
        foreach (var theme in _appVm.Themes.Read())
            themeCombo.Items.Add(new ComboBoxItem { Content = theme, Tag = theme });
        themeCombo.SelectedItem = themeCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string) == _bankThemeFilter) ?? themeCombo.Items[0];
        themeCombo.SelectionChanged += (_, _) =>
        {
            _bankThemeFilter = (themeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            RenderBank();
        };
        filters.Children.Add(themeCombo);

        var search = MakeTextBox(AppStrings.BankSearchPlaceholder, 220);
        search.Text = _bankSearch;
        search.TextChanged += (_, _) => { _bankSearch = search.Text; RefreshBankItems(); };
        filters.Children.Add(search);
        panel.Children.Add(filters);

        _bankItemsHost = new StackPanel { Spacing = 8 };
        panel.Children.Add(_bankItemsHost);
        RefreshBankItems();

        return panel;
    }

    private StackPanel? _bankItemsHost;

    private void RefreshBankItems()
    {
        if (_bankItemsHost is null) return;
        _bankItemsHost.Children.Clear();

        var matches = FilteredBankQuestions();
        if (matches.Count == 0)
        {
            _bankItemsHost.Children.Add(new TextBlock
            {
                Text = AppStrings.BankEmpty,
                Foreground = new SolidColorBrush(Colors.Gray),
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }
        foreach (var q in matches)
            _bankItemsHost.Children.Add(BuildBankListItem(q));
    }

    private IReadOnlyList<TestQuestion> FilteredBankQuestions()
    {
        IEnumerable<TestQuestion> q = _vm.Bank.Questions;
        if (!string.IsNullOrWhiteSpace(_bankThemeFilter))
            q = q.Where(x => string.Equals(x.Theme, _bankThemeFilter, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrWhiteSpace(_bankSearch))
        {
            var needle = _bankSearch.Trim();
            q = q.Where(x =>
                x.Text.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
                x.TagList.Any(t => t.Contains(needle, StringComparison.CurrentCultureIgnoreCase)));
        }
        return q.ToList();
    }

    private UIElement BuildBankListItem(TestQuestion q)
    {
        var card = new Grid
        {
            Padding = new Thickness(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(StimulusChip(q.Stimulus));
        head.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(q.Text) ? q.Id : q.Text,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        info.Children.Add(head);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Theme)) meta.Add(q.Theme!);
        if (q.TagList.Count > 0) meta.Add(string.Join(", ", q.TagList));
        meta.Add(AppStrings.TestCtorIdFormat(q.Id));
        info.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", meta),
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(info, 0);
        card.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        var edit = new Button { Content = AppStrings.BankEdit };
        edit.Click += (_, _) => { if (_vm.EditBankQuestion(q.Id)) RenderBank(); };
        actions.Children.Add(edit);
        var del = new Button { Content = AppStrings.BankDelete };
        del.Click += async (_, _) => await OnDeleteBankQuestionAsync(q);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 1);
        card.Children.Add(actions);

        return card;
    }

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

        // Stimulus type: text / image / ECG.
        card.Children.Add(BuildStimulusSelector(q, reRender));
        switch (q.Stimulus())
        {
            case QuestionStimulus.Ecg:
                card.Children.Add(BuildEcgPicker(q));
                break;
            case QuestionStimulus.Image:
                card.Children.Add(BuildImagePicker(q, reRender));
                break;
        }

        // Theme + tags.
        card.Children.Add(BuildThemeTagsRow(q));

        // Options.
        card.Children.Add(new TextBlock { Text = AppStrings.TestCtorCorrect, Opacity = 0.7, FontSize = 12 });
        for (var i = 0; i < q.Options.Count; i++)
            card.Children.Add(BuildOptionRow(q, q.Options[i], i + 1, reRender));
        var addOpt = new Button { Content = "+", MinWidth = 36, IsEnabled = q.Options.Count < TestConstructorViewModel.MaxOptions };
        addOpt.Click += (_, _) => { _vm.AddOption(q); reRender(); };
        card.Children.Add(addOpt);

        var comment = MakeMultilineBox(AppStrings.TestCtorComment, q.Comment);
        comment.TextChanged += (_, _) => { q.Comment = comment.Text; _vm.IsDirty = true; };
        card.Children.Add(comment);

        if (inTest)
        {
            var toBank = new Button { Content = AppStrings.TestCtorToBank, HorizontalAlignment = HorizontalAlignment.Left };
            toBank.Click += (_, _) =>
            {
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
        var current = q.Stimulus();
        RadioButton Make(string label, QuestionStimulus kind)
        {
            var rb = new RadioButton { Content = label, GroupName = group, IsChecked = current == kind, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            rb.Checked += (_, _) =>
            {
                if (q.Kind == kind) return;
                q.Kind = kind;
                _vm.IsDirty = true;
                reRender();
            };
            return rb;
        }
        stack.Children.Add(Make(AppStrings.TestCtorStimulusText, QuestionStimulus.Text));
        stack.Children.Add(Make(AppStrings.TestCtorStimulusImage, QuestionStimulus.Image));
        stack.Children.Add(Make(AppStrings.TestCtorStimulusEcg, QuestionStimulus.Ecg));
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
        Grid.SetColumn(tags, 1);
        grid.Children.Add(tags);

        return grid;
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

    private UIElement BuildEcgPicker(TestConstructorViewModel.EditQuestion q)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = AppStrings.TestCtorEcg, VerticalAlignment = VerticalAlignment.Center });

        var combo = new ComboBox { MinWidth = 220 };
        combo.Items.Add(new ComboBoxItem { Content = AppStrings.TestCtorEcgNone, Tag = null });
        foreach (var entry in _rhythmVm.Rhythms)
            combo.Items.Add(new ComboBoxItem { Content = EcgLabel(entry.Id), Tag = entry.Id });

        var suppress = true;
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string) == q.PathologyId) ?? combo.Items[0];
        suppress = false;

        combo.SelectionChanged += (_, _) =>
        {
            if (suppress) return;
            q.PathologyId = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            q.ImagePath = null;
            _vm.IsDirty = true;
            if (q.PathologyId is { } pid) RunPreview(pid);
            else SetPreviewRunning(false);
        };

        stack.Children.Add(combo);
        return stack;
    }

    private static UIElement StimulusChip(QuestionStimulus stimulus)
    {
        var label = stimulus switch
        {
            QuestionStimulus.Image => AppStrings.TestCtorStimulusImage,
            QuestionStimulus.Ecg => AppStrings.TestCtorStimulusEcg,
            _ => AppStrings.TestCtorStimulusText,
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 0x33, 0xA0, 0x6A)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 0x33, 0xA0, 0x6A)) },
        };
    }

    private string EcgLabel(string id)
    {
        var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return id;
        return _appVm.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;
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

        var content = new StackPanel { Spacing = 10, MinWidth = 320 };
        content.Children.Add(new ScrollViewer { Content = listHost, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(addRow);

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
}
