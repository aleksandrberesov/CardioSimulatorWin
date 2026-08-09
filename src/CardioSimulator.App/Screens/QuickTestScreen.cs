using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// The lecture context a <see cref="QuickTestScreen"/> reinforces. The host builds this from the
/// just-completed lecture: the section label / name, the subtopic id + title, the section's progress,
/// and the <see cref="Theme"/> used to filter ready tests and seed generation (null = no theme filter).
/// </summary>
public sealed record QuickTestContext(
    string SectionLabel,
    string SubtopicId,
    string SubtopicTitle,
    string SectionName,
    int SectionProgressPercent,
    string? Theme = null);

/// <summary>
/// Post-lecture "Quick test" launcher («Быстрый тест») — a native port of the prototype, built as a
/// reusable component for later wiring into the lecture-completion flow (Teaching mode). It shows the
/// completed topic + section progress, then lets the student either pick a <b>ready test</b> (from the
/// <see cref="TestRepository"/>, optionally filtered to the lecture's theme) or <b>generate</b> one on
/// the topic (test-type multi-select + count / time / difficulty, drawn from the question bank). It
/// raises <see cref="BackToLectureRequested"/> and <see cref="TestStartRequested"/> (with the chosen or
/// freshly-built <see cref="Test"/>); the host decides how to run it. Self-contained: theme-aware
/// (re-renders on <see cref="AppTheme.Changed"/>) and localized via <see cref="AppStrings"/>.
/// </summary>
public sealed class QuickTestScreen : UserControl
{
    // Data-viz badge colours (constant across themes).
    private static readonly Color Green = Color.FromArgb(0xFF, 0x1A, 0x8A, 0x6A);

    /// <summary>Raised when the student chooses to return to the lecture material.</summary>
    public event Action? BackToLectureRequested;

    /// <summary>Raised with the test to run — a selected ready test, or a freshly generated (ephemeral,
    /// unsaved) one. The host navigates to the Testing flow with it.</summary>
    public event Action<Test>? TestStartRequested;

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16),
    };

    private readonly Border _toast;
    private readonly TextBlock _toastTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _toastDesc = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private DispatcherQueueTimer? _toastTimer;

    private AppViewModel? _appVm;
    private QuickTestContext _context = new("", "", "", "", 0);

    // State.
    private string _action = "ready";           // "ready" | "generate"
    private string _filter = "all";             // "all" | "bytheme"
    private string? _selectedTestId;
    private readonly HashSet<string> _genTypes = new() { "questions" };
    private int _genCount = 10;
    private int _genTime = 15;
    private string _genDifficulty = "medium";   // "easy" | "medium" | "hard" | "mixed"
    private bool _welcomed;

    public QuickTestScreen()
    {
        _toastDesc.Foreground = AppTheme.TextSecondary;
        var toastStack = new StackPanel();
        toastStack.Children.Add(_toastTitle);
        toastStack.Children.Add(_toastDesc);
        _toast = new Border
        {
            Child = toastStack,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 20, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 24),
            MaxWidth = 380,
            Visibility = Visibility.Collapsed,
        };

        _root.Children.Add(_scroll);
        _root.Children.Add(_toast);
        Content = _root;

        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            AppTheme.Changed -= OnThemeChanged;
            _toastTimer?.Stop();
        };
    }

    /// <summary>Binds the launcher to the completed lecture's context and its data sources.</summary>
    public void Initialize(AppViewModel appVm, QuickTestContext context)
    {
        _appVm = appVm;
        _context = context;
        // Default selection: the first ready test (like the prototype).
        _selectedTestId = ReadyTests().FirstOrDefault()?.TestId;
        Render();

        if (!_welcomed)
        {
            _welcomed = true;
            ShowToast("👋", AppStrings.QuickWelcomeTitle, AppStrings.QuickWelcomeDesc);
        }
    }

    private void OnThemeChanged()
    {
        _toast.Background = AppTheme.AppCardBackground;
        _toastDesc.Foreground = AppTheme.TextSecondary;
        Render();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_appVm is null) return;

        var card = new StackPanel { Spacing = 16 };
        card.Children.Add(BuildHeader());
        card.Children.Add(Hairline());
        card.Children.Add(BuildTopicInfo());
        card.Children.Add(BuildActionSection());
        card.Children.Add(_action == "ready" ? BuildReadyTests() : BuildGenerator());
        card.Children.Add(BuildActionButtons());
        card.Children.Add(Hairline());
        card.Children.Add(new TextBlock
        {
            Text = AppStrings.QuickFooterFormat(SubtopicLabel()),
            FontSize = 11,
            Foreground = AppTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        _scroll.Content = new Border
        {
            Child = card,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(28, 24, 28, 20),
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private string SubtopicLabel() =>
        string.IsNullOrWhiteSpace(_context.SubtopicId) ? _context.SubtopicTitle
        : $"{_context.SubtopicId} {_context.SubtopicTitle}".Trim();

    // ── Header ──────────────────────────────────────────────────────────────

    private UIElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = AppStrings.QuickTitle, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary });
        titles.Children.Add(new TextBlock { Text = AppStrings.QuickSubtitle, FontSize = 13, Foreground = AppTheme.TextSecondary });
        Grid.SetColumn(titles, 0);
        grid.Children.Add(titles);

        if (!string.IsNullOrWhiteSpace(_context.SectionLabel))
        {
            var badge = new Border
            {
                Background = AppTheme.Accent,
                CornerRadius = new CornerRadius(30),
                Padding = new Thickness(16, 4, 16, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = _context.SectionLabel, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) },
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        return grid;
    }

    // ── Topic info ────────────────────────────────────────────────────────────

    private UIElement BuildTopicInfo()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: progress ring (only when known) + breadcrumb + name.
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };

        var hasProgress = _context.SectionProgressPercent >= 0;
        if (hasProgress)
        {
            var ringHost = new Grid { Width = 48, Height = 48, VerticalAlignment = VerticalAlignment.Center };
            ringHost.Children.Add(new ProgressRing
            {
                IsIndeterminate = false,
                Value = Math.Clamp(_context.SectionProgressPercent, 0, 100),
                Minimum = 0,
                Maximum = 100,
                Width = 48,
                Height = 48,
                Foreground = AppTheme.Accent,
            });
            ringHost.Children.Add(new TextBlock
            {
                Text = $"{_context.SectionProgressPercent}%",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = AppTheme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(ringHost);
        }

        var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        breadcrumb.Children.Add(new TextBlock { Text = _context.SectionLabel, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(SubtopicLabel()))
        {
            breadcrumb.Children.Add(new TextBlock { Text = "›", FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
            breadcrumb.Children.Add(new TextBlock { Text = SubtopicLabel(), FontSize = 12, Foreground = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        }
        details.Children.Add(breadcrumb);
        if (!string.IsNullOrWhiteSpace(_context.SectionName))
            details.Children.Add(new TextBlock { Text = _context.SectionName, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(details);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right: progress stat (only when known).
        if (hasProgress)
        {
            var stat = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stat.Children.Add(new TextBlock { Text = $"{_context.SectionProgressPercent}%", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center });
            stat.Children.Add(new TextBlock { Text = AppStrings.QuickProgressLabel, FontSize = 10, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(stat, 1);
            grid.Children.Add(stat);
        }

        return Card(grid, subtle: true, radius: 16);
    }

    // ── Action choice ───────────────────────────────────────────────────────

    private UIElement BuildActionSection()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = AppStrings.QuickActionLabel, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ready = ActionCard("ready", "📋", AppStrings.QuickActionReady, AppStrings.QuickActionReadyDesc);
        Grid.SetColumn(ready, 0);
        grid.Children.Add(ready);
        var gen = ActionCard("generate", "⚙️", AppStrings.QuickActionGenerate, AppStrings.QuickActionGenerateDesc);
        Grid.SetColumn(gen, 1);
        grid.Children.Add(gen);
        stack.Children.Add(grid);
        return stack;
    }

    private Button ActionCard(string action, string icon, string title, string desc)
    {
        var active = _action == action;
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock { Text = icon, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = active ? AppTheme.AppAccentSoftBackground : AppTheme.AppSubtleFill,
            BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
        };
        btn.Click += (_, _) => { _action = action; Render(); };
        return btn;
    }

    // ── Ready tests ─────────────────────────────────────────────────────────

    private IReadOnlyList<Test> ReadyTests()
    {
        if (_appVm is null) return Array.Empty<Test>();
        IEnumerable<Test> tests = _appVm.TestRepository.Tests;
        if (_filter == "bytheme" && !string.IsNullOrWhiteSpace(_context.Theme))
            tests = tests.Where(TestMatchesTheme);
        return tests.ToList();
    }

    private bool TestMatchesTheme(Test t) =>
        _context.Theme is { } th && t.Questions.Any(q => string.Equals(q.Theme, th, StringComparison.CurrentCultureIgnoreCase));

    private UIElement BuildReadyTests()
    {
        var stack = new StackPanel { Spacing = 10 };

        var tests = ReadyTests();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var h = new TextBlock { Text = AppStrings.QuickReadyHeader, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(h, 0);
        header.Children.Add(h);
        var count = new TextBlock { Text = AppStrings.QuickCountFormat(tests.Count), FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        stack.Children.Add(header);

        // Filter tags (All / By theme) — only when a theme is available to filter by.
        if (!string.IsNullOrWhiteSpace(_context.Theme))
        {
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            tags.Children.Add(FilterTag("all", AppStrings.QuickFilterAll));
            tags.Children.Add(FilterTag("bytheme", AppStrings.QuickFilterByTheme));
            stack.Children.Add(tags);
        }

        if (tests.Count == 0)
        {
            var empty = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 20, 0, 20) };
            empty.Children.Add(new TextBlock { Text = "📭", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = AppStrings.QuickReadyEmpty, FontSize = 14, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = AppStrings.QuickReadyEmptyHint, FontSize = 12, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(empty);
        }
        else
        {
            foreach (var t in tests)
                stack.Children.Add(BuildTestOption(t));
        }
        return stack;
    }

    private Border FilterTag(string key, string label)
    {
        var active = _filter == key;
        var border = new Border
        {
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(12, 3, 12, 3),
            BorderThickness = new Thickness(2),
            Background = active ? AppTheme.Accent : AppTheme.AppSubtleFill,
            BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder,
            Child = new TextBlock { Text = label, FontSize = 11, Foreground = active ? new SolidColorBrush(Colors.White) : AppTheme.TextSecondary },
        };
        border.PointerPressed += (_, _) =>
        {
            _filter = key;
            // Drop a selection that the new filter hides.
            if (_selectedTestId is { } id && ReadyTests().All(t => t.TestId != id)) _selectedTestId = null;
            Render();
        };
        return border;
    }

    private UIElement BuildTestOption(Test t)
    {
        var selected = t.TestId == _selectedTestId;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = t.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap });
        var minutes = t.QuestionTimeSeconds > 0 ? (int)Math.Round(t.QuestionTimeSeconds * t.Questions.Count / 60.0) : 0;
        var meta = minutes > 0 ? AppStrings.TestGenReadyMetaFormat(t.Questions.Count, minutes) : AppStrings.TestGenReadyUntimedFormat(t.Questions.Count);
        info.Children.Add(new TextBlock { Text = meta, FontSize = 12, Foreground = AppTheme.TextSecondary });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (TestMatchesTheme(t))
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x24, Green.R, Green.G, Green.B)),
                CornerRadius = new CornerRadius(30),
                Padding = new Thickness(10, 2, 10, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = AppStrings.QuickBadgeByTheme, FontSize = 10, Foreground = new SolidColorBrush(Green) },
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        var btn = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = selected ? AppTheme.AppAccentSoftBackground : AppTheme.AppSubtleFill,
            BorderBrush = selected ? AppTheme.Accent : AppTheme.AppCardBorder,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 3, 0, 0),
        };
        var id = t.TestId;
        btn.Click += (_, _) => { _selectedTestId = id; Render(); };
        return btn;
    }

    // ── Generator ─────────────────────────────────────────────────────────────

    private UIElement BuildGenerator()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = AppStrings.QuickGenLabel, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary });
        stack.Children.Add(new TextBlock { Text = AppStrings.QuickGenPickTypes, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });

        var types = new (string Key, string Icon, string Label, string Desc)[]
        {
            ("questions", "📝", AppStrings.TestGenTypeQuestions, AppStrings.TestGenTypeQuestionsDesc),
            ("image", "🖼️", AppStrings.TestGenTypeImage, AppStrings.TestGenTypeImageDesc),
            ("detect", "🔍", AppStrings.TestGenTypeDetect, AppStrings.TestGenTypeDetectDesc),
            ("assemble", "✏️", AppStrings.TestGenTypeAssemble, AppStrings.TestGenTypeAssembleDesc),
            ("case", "🏥", AppStrings.TestGenTypeClinical, AppStrings.TestGenTypeClinicalDesc),
            ("mixed", "🎯", AppStrings.QuickTypeMixed, AppStrings.QuickTypeMixedDesc),
        };
        var typeGrid = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < types.Length; i++)
            typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < types.Length; i++)
        {
            var t = types[i];
            var btn = TypeButton(t.Key, t.Icon, t.Label, t.Desc);
            Grid.SetColumn(btn, i);
            typeGrid.Children.Add(btn);
        }
        stack.Children.Add(typeGrid);

        // Params: count / time / difficulty.
        var paramsGrid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var count = ParamColumn(AppStrings.QuickCount, AppStrings.QuickCountHint, MakeNumberBox(_genCount, 5, 30, v => _genCount = v));
        Grid.SetColumn(count, 0);
        paramsGrid.Children.Add(count);

        var time = ParamColumn(AppStrings.QuickTime, AppStrings.QuickTimeHint, MakeNumberBox(_genTime, 5, 45, v => _genTime = v));
        Grid.SetColumn(time, 1);
        paramsGrid.Children.Add(time);

        var diffBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        diffBox.Items.Add(new ComboBoxItem { Content = AppStrings.DiffEasy, Tag = "easy" });
        diffBox.Items.Add(new ComboBoxItem { Content = AppStrings.DiffMedium, Tag = "medium" });
        diffBox.Items.Add(new ComboBoxItem { Content = AppStrings.DiffHard, Tag = "hard" });
        diffBox.Items.Add(new ComboBoxItem { Content = AppStrings.QuickDiffMixed, Tag = "mixed" });
        diffBox.SelectedItem = diffBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _genDifficulty) ?? diffBox.Items[1];
        diffBox.SelectionChanged += (_, _) => { if ((diffBox.SelectedItem as ComboBoxItem)?.Tag is string d) _genDifficulty = d; };
        var diff = ParamColumn(AppStrings.QuickDifficulty, AppStrings.QuickDifficultyHint, diffBox);
        Grid.SetColumn(diff, 2);
        paramsGrid.Children.Add(diff);
        stack.Children.Add(paramsGrid);

        return stack;
    }

    private Button TypeButton(string key, string icon, string label, string desc)
    {
        var active = _genTypes.Contains(key);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock { Text = icon, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = desc, FontSize = 9, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (active)
            content.Children.Add(new TextBlock { Text = "✓", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = AppTheme.Accent, HorizontalAlignment = HorizontalAlignment.Center });
        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = active ? AppTheme.AppAccentSoftBackground : AppTheme.AppCardBackground,
            BorderBrush = active ? AppTheme.Accent : AppTheme.AppCardBorder,
            BorderThickness = new Thickness(active ? 2 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(4, 10, 4, 10),
        };
        btn.Click += (_, _) =>
        {
            if (!_genTypes.Remove(key)) _genTypes.Add(key);
            if (_genTypes.Count == 0) _genTypes.Add(key);
            Render();
        };
        return btn;
    }

    private static StackPanel ParamColumn(string label, string hint, FrameworkElement input)
    {
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary });
        s.Children.Add(input);
        s.Children.Add(new TextBlock { Text = hint, FontSize = 9, Foreground = AppTheme.TextSecondary });
        return s;
    }

    private static NumberBox MakeNumberBox(int value, int min, int max, Action<int> onChange)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) onChange(Math.Clamp((int)e.NewValue, min, max)); };
        return box;
    }

    // ── Action buttons ──────────────────────────────────────────────────────

    private UIElement BuildActionButtons()
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var back = new Button { Content = AppStrings.QuickBackToLecture, HorizontalAlignment = HorizontalAlignment.Stretch };
        back.Click += (_, _) => BackToLectureRequested?.Invoke();
        Grid.SetColumn(back, 0);
        grid.Children.Add(back);

        var start = PrimaryButton(AppStrings.QuickStart);
        start.HorizontalAlignment = HorizontalAlignment.Stretch;
        start.Click += (_, _) => OnStart();
        Grid.SetColumn(start, 1);
        grid.Children.Add(start);
        return grid;
    }

    private void OnStart()
    {
        if (_appVm is null) return;

        if (_action == "ready")
        {
            var test = _selectedTestId is { } id ? _appVm.TestRepository.Test(id) : null;
            if (test is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrNoTest); return; }
            ShowToast("🚀", AppStrings.QuickStartedTitle, AppStrings.QuickStartedDescFormat(test.Title));
            TestStartRequested?.Invoke(test);
            return;
        }

        var generated = GenerateTest();
        if (generated is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrEmpty); return; }
        ShowToast("🚀", AppStrings.QuickStartedTitle, AppStrings.QuickStartedDescFormat(generated.Title));
        TestStartRequested?.Invoke(generated);
    }

    /// <summary>Builds an ephemeral test from the bank, filtered to the lecture's theme + selected types,
    /// softly honouring the chosen difficulty. Returns null when no question matches.</summary>
    private Test? GenerateTest()
    {
        if (_appVm is null) return null;
        var mixed = _genTypes.Count == 0 || _genTypes.Contains("mixed");

        bool TypeMatch(TestQuestion q) => mixed ||
            (_genTypes.Contains("questions") && !q.IsAssembly && q.Stimulus is QuestionStimulus.Text or QuestionStimulus.Ecg) ||
            (_genTypes.Contains("image") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Image) ||
            (_genTypes.Contains("detect") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Ecg) ||
            (_genTypes.Contains("assemble") && q.IsAssembly) ||
            (_genTypes.Contains("case") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Text);

        bool ThemeMatch(TestQuestion q) =>
            string.IsNullOrWhiteSpace(_context.Theme) ||
            (q.Theme is { } t && string.Equals(t, _context.Theme, StringComparison.CurrentCultureIgnoreCase));

        var candidates = _appVm.QuestionBank.Questions.Where(q => TypeMatch(q) && ThemeMatch(q)).ToList();
        // If the topic's theme is too narrow (no tagged questions yet), fall back to the whole bank so a
        // quick test can still be produced rather than failing.
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(_context.Theme))
            candidates = _appVm.QuestionBank.Questions.Where(TypeMatch).ToList();
        if (candidates.Count == 0) return null;

        // Difficulty is a soft preference: matching questions first, then the rest.
        var pool = candidates;
        if (DifficultyValue() is { } diff)
        {
            var preferred = candidates.Where(q => q.Difficulty == diff).ToList();
            if (preferred.Count > 0)
                pool = preferred.Concat(candidates.Where(q => q.Difficulty != diff)).ToList();
        }

        var rng = new Random();
        var ordered = pool.ToList();
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }
        var chosen = ordered.Take(_genCount).ToList();
        var perQuestion = (int)Math.Round(_genTime * 60.0 / chosen.Count);
        var questions = chosen.Select((q, i) => q with { Id = TestConstructorViewModel.NewId(), Number = i + 1 }).ToList();

        var title = $"{AppStrings.QuickTitle} · {SubtopicLabel()}".Trim();
        return new Test(TestConstructorViewModel.NewId(), title, questions, perQuestion);
    }

    private QuestionDifficulty? DifficultyValue() => _genDifficulty switch
    {
        "easy" => QuestionDifficulty.Easy,
        "hard" => QuestionDifficulty.Hard,
        "medium" => QuestionDifficulty.Medium,
        _ => null, // mixed
    };

    // ── Toast ─────────────────────────────────────────────────────────────────

    private void ShowToast(string emoji, string title, string desc)
    {
        _toastTitle.Text = string.IsNullOrEmpty(title) ? emoji : $"{emoji} {title}";
        _toastTitle.Foreground = AppTheme.TextPrimary;
        _toastDesc.Text = desc;
        _toast.Visibility = Visibility.Visible;

        _toastTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick -= OnToastTick;
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
    }

    private void OnToastTick(DispatcherQueueTimer sender, object args)
    {
        _toast.Visibility = Visibility.Collapsed;
        sender.Stop();
    }

    // ── Small helpers ───────────────────────────────────────────────────────

    private static Border Card(UIElement child, bool subtle = false, double radius = 16) => new()
    {
        Child = child,
        Background = subtle ? AppTheme.AppSubtleFill : AppTheme.AppCardBackground,
        BorderBrush = AppTheme.AppCardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(16, 14, 16, 14),
    };

    private static Border Hairline() => new()
    {
        Height = 1,
        Background = AppTheme.AppCardBorder,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static Button PrimaryButton(string content)
    {
        var btn = new Button { Content = content };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
            btn.Style = s;
        return btn;
    }
}
