using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Standalone Test Constructor — its own top-level operating mode (parallel to the Course / OSCE
/// constructors). A teacher authors self-assessment tests: title, per-question time limit, and a list
/// of single-choice questions (text, options, correct answer, explanation comment, and the ECG bound
/// to each question), with a live monitor preview of the selected ECG. Saved tests appear in the
/// Testing screen's picker.
/// </summary>
/// <remarks>
/// Like <see cref="OskeConstructorScreen"/>, the monitor is built once and kept permanently parented
/// (toggled via opacity/running, never re-parented): the Win2D <c>EcgMonitorControl</c> tears itself
/// down on <c>Unloaded</c>, so re-parenting it would destroy it and crash the XAML layer.
/// </remarks>
public sealed class TestConstructorScreen : UserControl
{
    private readonly TestConstructorViewModel _vm;
    private readonly MonitorViewModel _monitorVm;
    private readonly RhythmViewModel _rhythmVm;
    private readonly AppViewModel _appVm;
    private readonly MonitorView _monitor = new();

    private ComboBox _testsBox = null!;
    private TextBox _titleBox = null!;
    private TextBox _timeBox = null!;
    private Button _saveBtn = null!;
    private Button _deleteBtn = null!;
    private TextBlock _status = null!;
    private readonly ScrollViewer _editorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private TextBlock _intro = null!;
    private bool _suppressTests;

    public TestConstructorScreen(TestConstructorViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;

        Content = BuildLayout();
        _rhythmVm.PropertyChanged += OnRhythmChanged;
        Unloaded += (_, _) => _rhythmVm.PropertyChanged -= OnRhythmChanged;
        RenderEditor();
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The ECG pickers depend on the rhythm manifest; rebuild once it loads.
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms)) RenderEditor();
    }

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

        _testsBox = new ComboBox { MinWidth = 220, PlaceholderText = AppStrings.TestCtorTestsLabel, VerticalAlignment = VerticalAlignment.Center };
        var newBtn = new Button { Content = AppStrings.TestCtorNew };
        _deleteBtn = new Button { Content = AppStrings.TestCtorDelete, IsEnabled = false };

        _titleBox = MakeTextBox(AppStrings.TestCtorTitleLabel, 220);
        _timeBox = MakeTextBox(AppStrings.TestCtorTimeLabel, 120);

        _saveBtn = new Button { Content = AppStrings.TestCtorSave, IsEnabled = false };
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };

        toolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTestsLabel, VerticalAlignment = VerticalAlignment.Center });
        toolbar.Children.Add(_testsBox);
        toolbar.Children.Add(newBtn);
        toolbar.Children.Add(_deleteBtn);
        toolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTitleLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        toolbar.Children.Add(_titleBox);
        toolbar.Children.Add(new TextBlock { Text = AppStrings.TestCtorTimeLabel, VerticalAlignment = VerticalAlignment.Center });
        toolbar.Children.Add(_timeBox);
        toolbar.Children.Add(_saveBtn);
        toolbar.Children.Add(_status);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(_monitor, 0);
        body.Children.Add(_monitor);

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
        Grid.SetColumn(rightHost, 1);
        body.Children.Add(rightHost);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

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
        _deleteBtn.Click += async (_, _) => await OnDeleteAsync();

        return root;
    }

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

    private async Task OnDeleteAsync()
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

    // ── Editor ─────────────────────────────────────────────────────────────

    private void RenderEditor()
    {
        var has = _vm.HasTest;
        _intro.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _editorScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _saveBtn.IsEnabled = has;
        _deleteBtn.IsEnabled = has;

        if (!has)
        {
            _editorScroll.Content = null;
            _monitorVm.SetIsRunning(false);
            return;
        }

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(12, 8, 12, 8) };
        for (var i = 0; i < _vm.Questions.Count; i++)
            panel.Children.Add(BuildQuestionCard(_vm.Questions[i], i + 1));

        var add = new Button { Content = AppStrings.TestCtorAddQuestion };
        add.Click += (_, _) => { _vm.AddQuestion(); RenderEditor(); };
        panel.Children.Add(add);

        _editorScroll.Content = panel;

        // Preview the first question's bound ECG, if any.
        var firstWithEcg = _vm.Questions.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q.PathologyId));
        if (firstWithEcg?.PathologyId is { } pid) PreviewEcg(pid);
        else _monitorVm.SetIsRunning(false);
    }

    private UIElement BuildQuestionCard(TestConstructorViewModel.EditQuestion q, int number)
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
        var title = new TextBlock { Text = AppStrings.TestCtorQuestionLabelFormat(number), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var removeQ = new Button { Content = AppStrings.TestCtorRemoveQuestion };
        removeQ.Click += (_, _) => { _vm.RemoveQuestion(q); RenderEditor(); };
        Grid.SetColumn(removeQ, 1);
        header.Children.Add(removeQ);
        card.Children.Add(header);

        var text = MakeMultilineBox(AppStrings.TestCtorQuestionText, q.Text);
        text.TextChanged += (_, _) => { q.Text = text.Text; _vm.IsDirty = true; };
        card.Children.Add(text);

        // ECG picker (drives the monitor preview).
        card.Children.Add(BuildEcgPicker(q));

        // Options: radio (correct) + text + remove.
        var optsHeader = new TextBlock { Text = AppStrings.TestCtorCorrect, Opacity = 0.7, FontSize = 12 };
        card.Children.Add(optsHeader);
        for (var i = 0; i < q.Options.Count; i++)
            card.Children.Add(BuildOptionRow(q, q.Options[i], i + 1));

        var addOpt = new Button { Content = "+", MinWidth = 36 };
        addOpt.Click += (_, _) => { _vm.AddOption(q); RenderEditor(); };
        card.Children.Add(addOpt);

        var comment = MakeMultilineBox(AppStrings.TestCtorComment, q.Comment);
        comment.TextChanged += (_, _) => { q.Comment = comment.Text; _vm.IsDirty = true; };
        card.Children.Add(comment);

        return card;
    }

    private UIElement BuildOptionRow(TestConstructorViewModel.EditQuestion q, TestConstructorViewModel.EditOption opt, int number)
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

        var remove = new Button { Content = "✕", MinWidth = 36, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { _vm.RemoveOption(q, opt); RenderEditor(); };
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
            _vm.IsDirty = true;
            if (q.PathologyId is { } pid) PreviewEcg(pid);
        };

        stack.Children.Add(combo);
        return stack;
    }

    private void PreviewEcg(string pathologyId)
    {
        _rhythmVm.SelectRhythm(pathologyId, persist: false);
        _monitorVm.SetIsRunning(true);
    }

    private string EcgLabel(string id)
    {
        var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return id;
        return _appVm.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;
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
