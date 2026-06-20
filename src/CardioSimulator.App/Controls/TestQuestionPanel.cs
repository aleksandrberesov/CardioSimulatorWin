using System;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The right-hand question panel of the Testing screen — a faithful build of the prototype: a
/// «N из M» counter and a «M:SS» countdown on top, the «N вопрос» title, the question text, the
/// numbered answer options, and (after answering) the «Комментарий» block with the correct-answer
/// line and explanation, plus a «Следующий вопрос» button carrying a ✓/✗ verdict. Before a test is
/// started it shows a picker; when finished, a score summary. The <see cref="TestViewModel"/> holds
/// all state; this control only renders it and owns the once-a-second countdown timer.
/// </summary>
public sealed class TestQuestionPanel : UserControl
{
    private static readonly SolidColorBrush Accent = new(Color.FromArgb(255, 33, 118, 255));
    private static readonly SolidColorBrush CounterRed = new(Color.FromArgb(255, 220, 30, 30));
    private static readonly SolidColorBrush CorrectGreen = new(Color.FromArgb(255, 30, 160, 60));
    private static readonly SolidColorBrush WrongRed = new(Color.FromArgb(255, 210, 40, 40));
    private static readonly SolidColorBrush Muted = new(Color.FromArgb(150, 128, 128, 128));

    private readonly Border _host = new() { Padding = new Thickness(16) };

    private TestViewModel? _vm;
    private TestRepository? _repo;
    private TextBlock? _timerText;
    private DispatcherQueueTimer? _timer;

    public TestQuestionPanel()
    {
        Content = _host;
        Unloaded += (_, _) => Teardown();
    }

    public void Bind(TestViewModel vm, TestRepository repo)
    {
        _vm = vm;
        _repo = repo;
        vm.StateChanged += OnStateChanged;
        vm.TimerTicked += OnTimerTicked;
        Render();
    }

    private void Teardown()
    {
        _timer?.Stop();
        if (_vm is null) return;
        _vm.StateChanged -= OnStateChanged;
        _vm.TimerTicked -= OnTimerTicked;
    }

    private void OnStateChanged() => Render();

    private void OnTimerTicked()
    {
        if (_timerText is not null && _vm is not null)
            _timerText.Text = FormatTime(_vm.RemainingSeconds);
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    private void Render()
    {
        if (_vm is null) return;

        if (!_vm.HasActiveTest && !_vm.Finished) _host.Child = BuildPicker();
        else if (_vm.Finished) _host.Child = BuildResult();
        else _host.Child = BuildActive();

        ManageTimer();
    }

    private void ManageTimer()
    {
        var run = _vm is { HasActiveTest: true, Revealed: false, IsTimed: true };
        if (run)
        {
            EnsureTimer();
            _timer!.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    private void EnsureTimer()
    {
        if (_timer is not null) return;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _vm?.Tick();
    }

    private UIElement BuildPicker()
    {
        var stack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.TestSelectTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var tests = _repo?.Tests ?? Array.Empty<Test>();
        if (tests.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = AppStrings.TestEmpty,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Muted,
            });
            return stack;
        }

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260 };
        foreach (var t in tests)
            combo.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.TestId });
        combo.SelectedIndex = 0;
        stack.Children.Add(combo);

        var start = new Button
        {
            Content = AppStrings.TestStart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        start.Click += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string id &&
                _repo?.Test(id) is { } test && test.Questions.Count > 0)
                _vm?.Start(test);
        };
        stack.Children.Add(start);
        return stack;
    }

    private UIElement BuildActive()
    {
        var vm = _vm!;
        var q = vm.Current!;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        // Header: counter (left) + countdown (right).
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var counter = new TextBlock
        {
            Text = AppStrings.TestCounterFormat(vm.Index + 1, vm.Count),
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Foreground = CounterRed,
        };
        Grid.SetColumn(counter, 0);
        header.Children.Add(counter);
        if (vm.IsTimed)
        {
            _timerText = new TextBlock
            {
                Text = FormatTime(vm.RemainingSeconds),
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                Foreground = CounterRed,
            };
            Grid.SetColumn(_timerText, 1);
            header.Children.Add(_timerText);
        }
        else
        {
            _timerText = null;
        }
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Title: «N вопрос».
        var title = new TextBlock
        {
            Text = AppStrings.TestQuestionTitleFormat(q.Number),
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Foreground = Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(title, 1);
        grid.Children.Add(title);

        // Body: question + options + comment.
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = q.Text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = Accent,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        for (var i = 0; i < q.Options.Count; i++)
            body.Children.Add(BuildOption(q, q.Options[i], i + 1));

        if (vm.Revealed)
            body.Children.Add(BuildComment(q));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        // Footer: Next/Finish + verdict glyph.
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var next = new Button
        {
            Content = vm.IsLastQuestion ? AppStrings.TestFinish : AppStrings.TestNext,
            IsEnabled = vm.Revealed,
            MinWidth = 180,
        };
        next.Click += (_, _) => vm.Next();
        footer.Children.Add(next);
        if (vm.Revealed)
        {
            footer.Children.Add(new TextBlock
            {
                Text = vm.AnswerCorrect ? "✓" : "✗",
                Foreground = vm.AnswerCorrect ? CorrectGreen : WrongRed,
                FontWeight = FontWeights.Bold,
                FontSize = 26,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return grid;
    }

    private UIElement BuildOption(TestQuestion q, TestOption opt, int number)
    {
        var vm = _vm!;
        var label = new TextBlock
        {
            Text = $"{number}. {opt.Text}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
        };

        if (!vm.Revealed)
        {
            label.Foreground = Accent;
        }
        else if (opt.Id == q.CorrectOptionId)
        {
            label.Foreground = CorrectGreen;
            label.FontWeight = FontWeights.SemiBold;
        }
        else if (opt.Id == vm.SelectedOptionId)
        {
            label.Foreground = WrongRed;
        }
        else
        {
            label.Foreground = Muted;
        }

        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(0, 1, 0, 1),
        };
        if (!vm.Revealed)
        {
            var oid = opt.Id;
            button.Click += (_, _) => vm.Select(oid);
        }
        return button;
    }

    private static UIElement BuildComment(TestQuestion q)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.TestCommentTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = Accent,
        });
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.TestCorrectAnswerFormat(q.CorrectOptionNumber()),
            FontWeight = FontWeights.SemiBold,
            Foreground = Accent,
        });
        if (!string.IsNullOrWhiteSpace(q.Comment))
        {
            panel.Children.Add(new TextBlock
            {
                Text = q.Comment,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Accent,
            });
        }
        return panel;
    }

    private UIElement BuildResult()
    {
        var vm = _vm!;
        var stack = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.TestResultTitle,
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var pass = vm.Count > 0 && vm.CorrectCount * 2 >= vm.Count;
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.TestResultScoreFormat(vm.CorrectCount, vm.Count),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = pass ? CorrectGreen : WrongRed,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        var restart = new Button { Content = AppStrings.TestRestart };
        restart.Click += (_, _) => vm.Restart();
        buttons.Children.Add(restart);
        var choose = new Button { Content = AppStrings.TestSelectTitle };
        choose.Click += (_, _) => vm.Close();
        buttons.Children.Add(choose);
        stack.Children.Add(buttons);
        return stack;
    }

    private static string FormatTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}
