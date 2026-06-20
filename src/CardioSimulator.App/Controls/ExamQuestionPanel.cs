using System;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
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
/// The exam question panel — the <see cref="TestQuestionPanel"/> flow without feedback: a «N из M»
/// counter and «M:SS» countdown, the «N вопрос» title, the question text, and the numbered options the
/// student selects (the chosen one is highlighted, but no correctness, no comment). The «Следующий
/// вопрос» / «Завершить» button advances; grading + the result view live in the
/// <see cref="Screens.ExaminationScreen"/>. Renders only while an attempt is in progress.
/// </summary>
public sealed class ExamQuestionPanel : UserControl
{
    private static readonly SolidColorBrush Accent = new(Color.FromArgb(255, 33, 118, 255));
    private static readonly SolidColorBrush CounterRed = new(Color.FromArgb(255, 220, 30, 30));
    private static readonly SolidColorBrush Neutral = new(Color.FromArgb(230, 120, 120, 120));

    private readonly Border _host = new() { Padding = new Thickness(16) };

    private ExaminationViewModel? _vm;
    private TextBlock? _timerText;
    private DispatcherQueueTimer? _timer;

    public ExamQuestionPanel()
    {
        Content = _host;
        Unloaded += (_, _) => Teardown();
    }

    public void Bind(ExaminationViewModel vm)
    {
        _vm = vm;
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

    private void Render()
    {
        if (_vm is null) return;
        _host.Child = _vm.IsTakingExam && _vm.Current is not null ? BuildActive() : null;
        ManageTimer();
    }

    private void ManageTimer()
    {
        if (_vm is { IsTakingExam: true, IsTimed: true })
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

    private UIElement BuildActive()
    {
        var vm = _vm!;
        var q = vm.Current!;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: counter + countdown.
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
        var selected = vm.SelectedFor(q.Id);
        for (var i = 0; i < q.Options.Count; i++)
            body.Children.Add(BuildOption(q.Options[i], i + 1, q.Options[i].Id == selected));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var next = new Button
        {
            Content = vm.IsLastQuestion ? AppStrings.ExamFinish : AppStrings.TestNext,
            MinWidth = 180,
        };
        next.Click += (_, _) => vm.Next();
        footer.Children.Add(next);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return grid;
    }

    private UIElement BuildOption(TestOption opt, int number, bool isSelected)
    {
        var label = new TextBlock
        {
            Text = $"{number}. {opt.Text}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Foreground = isSelected ? Accent : Neutral,
            FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
        };
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
        var oid = opt.Id;
        button.Click += (_, _) => _vm?.Select(oid);
        return button;
    }

    private static string FormatTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}
