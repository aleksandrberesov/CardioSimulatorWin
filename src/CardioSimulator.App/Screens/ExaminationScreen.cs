using System;
using System.Collections.Generic;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Examination («Экзамен») screen. Built the same way as the Testing screen — a per-question flow with
/// the monitor driven by the current question — but as a graded assessment: no comments/feedback, and
/// the attempt is graded and saved at the end, then viewable. A sub-tab bar (Экзамен / Результаты)
/// hosts the exam flow (a start dialog collects ФИО + группа + test, then the monitor on the left and
/// the <see cref="ExamQuestionPanel"/> on the right) and the saved-results list, mirroring the OSCE
/// station's storage + results pattern (<see cref="ExamResultStore"/> / <see cref="ExamGrader"/>).
/// </summary>
/// <remarks>
/// Like <see cref="OSKEScreen"/>, the exam/start/results areas are built once and toggled via
/// <see cref="UIElement.Visibility"/> rather than swapped in/out of the tree: the Win2D
/// <c>EcgMonitorControl</c> tears itself down on <c>Unloaded</c>, so re-parenting it would crash the
/// XAML layer. The question panel is kept permanently parented for the same reason (its countdown
/// subscription stays live across the take → graded → new-attempt cycle).
/// </remarks>
public sealed class ExaminationScreen : UserControl
{
    private ExaminationViewModel? _vm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private string? _loadedQuestionId;

    private readonly MonitorView _monitor = new();
    private readonly Image _stimulusImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly ExamQuestionPanel _questionPanel = new();
    private readonly Grid _root = new();
    private Button _examTab = null!;
    private Button _resultsTab = null!;
    private string _tab = "exam";

    private readonly Grid _contentArea = new();
    private FrameworkElement _startArea = null!;
    private Grid _examArea = null!;
    private readonly ScrollViewer _breakdownScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _resultsArea = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    public ExaminationScreen()
    {
        Content = BuildShell();
    }

    public void Initialize(ExaminationViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
        _questionPanel.Bind(vm);

        vm.StateChanged += OnVmStateChanged;
        Unloaded += (_, _) => vm.StateChanged -= OnVmStateChanged;

        ShowTab("exam");
    }

    private void OnVmStateChanged()
    {
        if (_tab == "exam") UpdateExamView();
    }

    // ── Shell + tabs ───────────────────────────────────────────────────────

    private UIElement BuildShell()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(12, 8, 12, 8) };
        _examTab = TabButton(AppStrings.ExamTabExam, () => ShowTab("exam"));
        _resultsTab = TabButton(AppStrings.ExamTabResults, () => ShowTab("results"));
        tabBar.Children.Add(_examTab);
        tabBar.Children.Add(_resultsTab);
        Grid.SetRow(tabBar, 0);
        _root.Children.Add(tabBar);

        BuildContentArea();
        Grid.SetRow(_contentArea, 1);
        _root.Children.Add(_contentArea);
        return _root;
    }

    private void BuildContentArea()
    {
        // Start area: centered intro + "Start" button.
        var startStack = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        startStack.Children.Add(new TextBlock
        {
            Text = AppStrings.ExamIntro,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
        });
        var startBtn = new Button { Content = AppStrings.ExamStart, HorizontalAlignment = HorizontalAlignment.Center };
        startBtn.Click += async (_, _) => await OnStartAsync();
        startStack.Children.Add(startBtn);
        _startArea = startStack;

        // Exam area: monitor (left) + question panel / graded breakdown (right). Built once.
        _examArea = new Grid { Visibility = Visibility.Collapsed };
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        // Left column: monitor and image stimulus share the slot (one visible at a time); the monitor
        // is never re-parented (its Win2D canvas tears down on Unloaded), only toggled by Visibility.
        var left = new Grid();
        left.Children.Add(_monitor);
        _stimulusImage.Visibility = Visibility.Collapsed;
        left.Children.Add(_stimulusImage);
        Grid.SetColumn(left, 0);
        _examArea.Children.Add(left);

        var right = new Grid();
        Grid.SetColumn(right, 1);
        _breakdownScroll.Visibility = Visibility.Collapsed;
        right.Children.Add(_questionPanel);
        right.Children.Add(_breakdownScroll);
        _examArea.Children.Add(right);

        _resultsArea.Visibility = Visibility.Collapsed;

        _contentArea.Children.Add(_startArea);
        _contentArea.Children.Add(_examArea);
        _contentArea.Children.Add(_resultsArea);
    }

    private static Button TabButton(string text, Action onClick)
    {
        var btn = new Button { Content = text, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ShowTab(string tab)
    {
        _tab = tab;
        _examTab.FontWeight = tab == "exam" ? FontWeights.Bold : FontWeights.Normal;
        _resultsTab.FontWeight = tab == "results" ? FontWeights.Bold : FontWeights.Normal;

        if (tab == "results")
        {
            _resultsArea.Content = BuildResultsContent();
            _startArea.Visibility = Visibility.Collapsed;
            _examArea.Visibility = Visibility.Collapsed;
            _resultsArea.Visibility = Visibility.Visible;
            ParkStimulus();
        }
        else
        {
            UpdateExamView();
        }
    }

    // ── Exam view (start / taking / graded) ─────────────────────────────────

    private void UpdateExamView()
    {
        _resultsArea.Visibility = Visibility.Collapsed;

        // Start state: nothing in progress and nothing graded.
        if (_vm is null || (_vm.Result is null && !_vm.IsTakingExam))
        {
            _startArea.Visibility = Visibility.Visible;
            _examArea.Visibility = Visibility.Collapsed;
            _loadedQuestionId = null;
            ParkStimulus();
            return;
        }

        _startArea.Visibility = Visibility.Collapsed;
        _examArea.Visibility = Visibility.Visible;

        var graded = _vm.Result is not null;
        _questionPanel.Visibility = graded ? Visibility.Collapsed : Visibility.Visible;
        _breakdownScroll.Visibility = graded ? Visibility.Visible : Visibility.Collapsed;

        if (graded)
        {
            _breakdownScroll.Content = BuildBreakdown(_vm.Result!, _vm.Test, showNewAttempt: true);
            _loadedQuestionId = null;
            ParkStimulus();
            return;
        }

        // Taking: mirror the current question's stimulus onto the left pane (once per question).
        var q = _vm.Current;
        if (q is not null && q.Id != _loadedQuestionId)
        {
            _loadedQuestionId = q.Id;
            ApplyStimulus(q);
        }
    }

    /// <summary>Parks the left pane: monitor visible but stopped, image hidden (used when no question
    /// is being shown — start, graded, or results).</summary>
    private void ParkStimulus()
    {
        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;
        _monitor.Visibility = Visibility.Visible;
        _monitorVm?.SetIsRunning(false);
    }

    private void ApplyStimulus(TestQuestion q)
    {
        if (q.Stimulus == QuestionStimulus.Image && TestImageStore.UriFor(q.ImagePath) is { } uri)
        {
            _stimulusImage.Source = new BitmapImage(uri);
            _stimulusImage.Visibility = Visibility.Visible;
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
            return;
        }

        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;

        if (q.Stimulus == QuestionStimulus.Ecg && q.PathologyId is { } pathologyId && _rhythmVm is not null && _monitorVm is not null)
        {
            _monitor.Visibility = Visibility.Visible;
            _rhythmVm.SelectRhythm(pathologyId, persist: false);
            _monitorVm.SetLeadSelection(q.LeadList);
            _monitorVm.SetSeriesScheme(q.Scheme);
            _monitorVm.SetIsRunning(true);
        }
        else
        {
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
        }
    }

    // ── Grading breakdown (shared by post-submit + saved results) ───────────

    private UIElement BuildBreakdown(ExamResult result, Test? test, bool showNewAttempt)
    {
        var lookup = test?.Questions.ToDictionary(q => q.Id);
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(12) };
        panel.Children.Add(BuildResultBanner(result));

        var number = 1;
        foreach (var r in result.Questions)
        {
            TestQuestion? q = null;
            lookup?.TryGetValue(r.QuestionId, out q);
            panel.Children.Add(BuildGradedQuestion(r, q, number++));
        }

        if (showNewAttempt)
        {
            var newBtn = new Button { Content = AppStrings.ExamNewAttempt, Margin = new Thickness(0, 8, 0, 0) };
            newBtn.Click += (_, _) => _vm?.Reset();
            panel.Children.Add(newBtn);
        }
        return panel;
    }

    private static UIElement BuildGradedQuestion(ExamQuestionResult r, TestQuestion? q, int number)
    {
        var block = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = r.IsCorrect ? "✓" : "✗",
            Foreground = new SolidColorBrush(r.IsCorrect ? Colors.LimeGreen : Colors.Tomato),
            FontWeight = FontWeights.Bold,
        });
        header.Children.Add(new TextBlock
        {
            Text = q is null ? $"{number}." : $"{number}. {q.Text}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        block.Children.Add(header);

        // Show the correct option and (if different/wrong) the student's pick.
        var ids = q is null
            ? new[] { r.Correct, r.Selected }.Where(id => id is not null).Select(id => id!).Distinct()
            : q.Options.Select(o => o.Id).Where(id => id == r.Correct || id == r.Selected);

        foreach (var id in ids)
        {
            var isCorrect = id == r.Correct;
            var color = isCorrect ? Colors.LimeGreen : Colors.Tomato;
            var text = q?.Options.FirstOrDefault(o => o.Id == id)?.Text ?? id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = isCorrect ? "✓" : "✗", Foreground = new SolidColorBrush(color), Width = 14 });
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(color) });
            block.Children.Add(row);
        }

        if (r.Selected is null)
        {
            block.Children.Add(new TextBlock
            {
                Text = AppStrings.ExamUnanswered,
                Margin = new Thickness(16, 0, 0, 0),
                Opacity = 0.7,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
            });
        }
        return block;
    }

    private static FrameworkElement BuildResultBanner(ExamResult res)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(res.Passed ? Color.FromArgb(40, 0, 200, 0) : Color.FromArgb(40, 220, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = res.Passed ? AppStrings.ExamPassed : AppStrings.ExamFailed,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(res.Passed ? Colors.LimeGreen : Colors.Tomato),
        });
        stack.Children.Add(new TextBlock { Text = AppStrings.ExamScoreFormat(res.CorrectCount, res.TotalCount) });
        stack.Children.Add(new TextBlock { Text = $"{res.Student.FullName} · {res.Student.Group}", Opacity = 0.7, FontSize = 12 });
        border.Child = stack;
        return border;
    }

    // ── Results tab ────────────────────────────────────────────────────────

    private UIElement BuildResultsContent()
    {
        if (_appVm is null) return new Grid();
        var results = _appVm.ExamResultStore.List();
        if (results.Count == 0) return Placeholder(AppStrings.ExamResultsEmpty);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8) };
        foreach (var r in results)
            list.Items.Add(new ListViewItem { Content = BuildResultListItem(r), Tag = r });
        Grid.SetColumn(list, 0);
        grid.Children.Add(list);

        var detail = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8),
        };
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem item && item.Tag is ExamResult r)
            {
                var test = _appVm.TestRepository.Test(r.TestId);
                detail.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = BuildBreakdown(r, test, showNewAttempt: false),
                };
            }
        };
        list.SelectedIndex = 0;
        return grid;
    }

    private static UIElement BuildResultListItem(ExamResult r)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = r.Student.FullName, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = $"{r.Student.Group} · {r.TestTitle} · {r.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}",
            Opacity = 0.8,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{(r.Passed ? AppStrings.ExamPassed : AppStrings.ExamFailed)} — {AppStrings.ExamScoreFormat(r.CorrectCount, r.TotalCount)}",
            Foreground = new SolidColorBrush(r.Passed ? Colors.LimeGreen : Colors.Tomato),
            FontSize = 12,
        });
        return panel;
    }

    private static UIElement Placeholder(string text) => new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Colors.Gray),
    };

    // ── Start flow ─────────────────────────────────────────────────────────

    private async Task OnStartAsync()
    {
        if (_vm is null || _appVm is null) return;
        var picked = await ShowStartDialogAsync();
        if (picked is null) return;
        _vm.Start(picked.Value.test, picked.Value.student);
        UpdateExamView();
    }

    private async Task<(ExamStudentInfo student, Test test)?> ShowStartDialogAsync()
    {
        var fio = new TextBox { Header = AppStrings.ExamFieldFullName, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var group = new TextBox { Header = AppStrings.ExamFieldGroup, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };

        var testBox = new ComboBox { Header = AppStrings.ExamFieldTest, HorizontalAlignment = HorizontalAlignment.Stretch };
        var tests = _appVm!.TestRepository.Tests;
        foreach (var t in tests)
            testBox.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.TestId });
        if (tests.Count > 0) testBox.SelectedIndex = 0;

        var hint = new TextBlock
        {
            Text = AppStrings.ExamNoTests,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.Tomato),
            Visibility = tests.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(fio);
        panel.Children.Add(group);
        panel.Children.Add(testBox);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = AppStrings.ExamStartTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.ExamStart,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) &&
            !string.IsNullOrWhiteSpace(group.Text) &&
            testBox.SelectedItem is ComboBoxItem;

        fio.TextChanged += (_, _) => Revalidate();
        group.TextChanged += (_, _) => Revalidate();
        testBox.SelectionChanged += (_, _) => Revalidate();
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if (testBox.SelectedItem is not ComboBoxItem item || item.Tag is not string testId) return null;
        if (_appVm.TestRepository.Test(testId) is not { } test || test.Questions.Count == 0) return null;

        return (new ExamStudentInfo(fio.Text.Trim(), group.Text.Trim()), test);
    }
}
