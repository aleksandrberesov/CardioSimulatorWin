using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Network;
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
    string? Theme = null,
    string? Subsection = null);

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

    /// <summary>Raised in group-configure mode (<see cref="InitializeGroupMode"/>) when the instructor
    /// confirms the setup. Carries a per-participant test factory (a shared ready test, or a fresh
    /// generated draw per student) instead of a single test — the host starts the group session with it.</summary>
    public event Action<GroupTestConfig>? GroupSessionRequested;

    private readonly Grid _root = new();
    private readonly Border _cardContainer;
    private readonly Grid _cardGrid = new();
    private readonly StackPanel _topStack = new() { Spacing = 14 };
    private readonly ScrollViewer _itemsScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly StackPanel _bottomStack = new() { Spacing = 12 };

    private readonly Border _toast;
    private readonly TextBlock _toastTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _toastDesc = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private DispatcherQueueTimer? _toastTimer;

    private AppViewModel? _appVm;
    private QuickTestContext _context = new("", "", "", "", 0);

    /// <summary>The taxonomy acronyms taught under the lecture's subsection — the precise signal for
    /// pulling the "right" questions. Empty when the lecture has no subsection mapping, in which case
    /// matching falls back to the free-text <see cref="QuickTestContext.Theme"/>.</summary>
    private HashSet<string> _lectureAcronyms = new(StringComparer.OrdinalIgnoreCase);

    // State.
    private string _action = "ready";           // "ready" | "generate"
    private string _filter = "all";             // "all" | "bytheme"
    private string? _selectedTestId;
    private readonly HashSet<string> _genTypes = new() { "questions" };
    private int _genCount = 10;
    private int _genTime = 15;
    private string _genDifficulty = "medium";   // "easy" | "medium" | "hard" | "mixed"
    private bool _welcomed;
    private bool _showWelcome;                   // one-time greeting shown as an in-flow banner (see Render)

    // Course-wide launcher mode (Testing / Examination entry): no single-lecture context. A theme
    // selector over all course themes replaces the lecture "by theme" filter and scopes both the
    // ready-test list and generation; the host supplies the header + button labels.
    private bool _courseMode;
    private string _courseTitle = "";
    private string _courseSubtitle = "";
    private string? _startLabel;
    private string? _backLabel;                 // null = single full-width Start (no secondary button)
    private IReadOnlyList<CourseThemeCatalog.Section> _themes = Array.Empty<CourseThemeCatalog.Section>();
    private string? _selectedTheme;             // null = all themes

    // Group-configure mode: the launcher is used as a Group-session setup panel. Identical customization
    // to course mode, but Start raises <see cref="GroupSessionRequested"/> with a per-participant test
    // factory rather than running a single test locally. Implies <see cref="_courseMode"/>.
    private bool _groupConfigure;

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
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 380,
            Visibility = Visibility.Collapsed,
        };

        _cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_topStack, 0);
        _cardGrid.Children.Add(_topStack);

        Grid.SetRow(_itemsScroll, 1);
        _cardGrid.Children.Add(_itemsScroll);

        Grid.SetRow(_bottomStack, 2);
        _cardGrid.Children.Add(_bottomStack);

        // The toast floats at the bottom of the scrollable content region (row 1), NOT over the whole
        // card — anchoring it to the card/screen bottom-right made the welcome hint sit on top of the
        // primary Start button in the bottom action band. Added last so it composites above the scroller.
        Grid.SetRow(_toast, 1);
        _cardGrid.Children.Add(_toast);

        _cardContainer = new Border
        {
            Child = _cardGrid,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(28, 24, 28, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(16),
        };

        _root.Children.Add(_cardContainer);
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
        _lectureAcronyms = LectureAcronyms(context);
        // Default selection: the first ready test (like the prototype).
        _selectedTestId = ReadyTests().FirstOrDefault()?.TestId;

        // Greet once per session. Shown as an in-flow banner at the top of the card (see Render) rather
        // than a floating toast: the dialog is packed edge-to-edge, so an overlay inevitably covers the
        // action cards, test list, or Start button. The banner takes its own space and auto-clears.
        if (!_welcomed)
        {
            _welcomed = true;
            _showWelcome = true;
        }
        Render();
    }

    /// <summary>
    /// Binds the launcher as a course-wide test picker for the Testing / Examination entry screens —
    /// no single-lecture context. A theme selector (all course themes, default «all») replaces the
    /// lecture «by theme» filter and scopes both the ready-test list and generation. The host supplies
    /// the header (<paramref name="title"/> / <paramref name="subtitle"/>) and the primary
    /// (<paramref name="startLabel"/>) / secondary (<paramref name="backLabel"/>, null = hidden) button
    /// labels. Choosing a ready or generated test raises <see cref="TestStartRequested"/>; the secondary
    /// button raises <see cref="BackToLectureRequested"/>. No welcome toast is shown.
    /// </summary>
    public void InitializeCourseMode(AppViewModel appVm, string title, string subtitle, string startLabel, string? backLabel)
    {
        _appVm = appVm;
        _courseMode = true;
        _groupConfigure = false;
        _courseTitle = title;
        _courseSubtitle = subtitle;
        _startLabel = startLabel;
        _backLabel = backLabel;
        _context = new QuickTestContext("", "", "", "", -1);
        _lectureAcronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Themes come from the loaded course package (its sections / sub-topics) — the same catalog the
        // Test Constructor authors questions against — not a hand-managed global list.
        _themes = CourseThemeCatalog.Sections(appVm.CourseRepository, appVm.SelectedLanguage);
        _selectedTheme = null;
        _selectedTestId = ReadyTests().FirstOrDefault()?.TestId;
        Render();
    }

    /// <summary>
    /// Binds the launcher as the setup panel for a <b>Group</b> session — identical customization to the
    /// Individual course picker (ready test / generate over all course themes). The difference is the
    /// primary action: instead of running one test locally, Start raises <see cref="GroupSessionRequested"/>
    /// with a factory the server calls once per registrant (a shared ready test, or a fresh generated draw
    /// per student). This is the parity the classroom flow requires: the same test setup as Individual.
    /// </summary>
    public void InitializeGroupMode(AppViewModel appVm, string title, string subtitle, string startLabel, string? backLabel)
    {
        InitializeCourseMode(appVm, title, subtitle, startLabel, backLabel);
        _groupConfigure = true;
    }

    private void OnThemeChanged()
    {
        _toast.Background = AppTheme.AppCardBackground;
        _toastDesc.Foreground = AppTheme.TextSecondary;
        if (_cardContainer is not null)
        {
            _cardContainer.Background = AppTheme.AppCardBackground;
            _cardContainer.BorderBrush = AppTheme.AppCardBorder;
        }
        Render();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_appVm is null) return;

        _topStack.Children.Clear();
        _topStack.Children.Add(BuildHeader());
        _topStack.Children.Add(Hairline());
        // The one-time welcome greeting sits here in the layout flow — above the topic/action content —
        // so it never overlaps the interactive cards, test list, or Start button. Cleared by the timer.
        if (_showWelcome)
            _topStack.Children.Add(BuildWelcomeBanner());
        // Lecture mode shows the completed-topic progress card; course mode swaps it for a theme
        // selector that scopes both the ready-test list and generation. That selector leads — placed
        // ABOVE the action cards and enlarged (customer request 28-08-2026: «Строчку ТЕМА вынести вверх
        // и сделать побольше, над блоками "Готовый тест" и "Сгенерировать"»).
        if (!_courseMode)
            _topStack.Children.Add(BuildTopicInfo());
        if (_courseMode)
            _topStack.Children.Add(BuildThemeSelector());
        _topStack.Children.Add(BuildActionSection());
        if (_action == "ready")
            _topStack.Children.Add(BuildReadyTestsHeader());

        _itemsScroll.Margin = new Thickness(0, 8, 0, 8);
        _itemsScroll.Content = _action == "ready" ? BuildReadyTestsList() : BuildGenerator();

        _bottomStack.Children.Clear();
        _bottomStack.Children.Add(Hairline());
        _bottomStack.Children.Add(BuildActionButtons());
        if (!_courseMode)
        {
            _bottomStack.Children.Add(new TextBlock
            {
                Text = AppStrings.QuickFooterFormat(SubtopicLabel()),
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
        }
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
        titles.Children.Add(new TextBlock { Text = _courseMode ? _courseTitle : AppStrings.QuickTitle, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary });
        titles.Children.Add(new TextBlock { Text = _courseMode ? _courseSubtitle : AppStrings.QuickSubtitle, FontSize = 13, Foreground = AppTheme.TextSecondary });
        Grid.SetColumn(titles, 0);
        grid.Children.Add(titles);

        if (!_courseMode && !string.IsNullOrWhiteSpace(_context.SectionLabel))
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
        if (_courseMode)
        {
            if (_selectedTheme is { } theme)
                tests = tests.Where(t => TestMatchesTheme(t, theme));
        }
        else if (_filter == "bytheme" && HasLectureSignal)
        {
            tests = tests.Where(TestMatchesLecture);
        }
        return tests.ToList();
    }

    /// <summary>A ready test belongs to a course theme when any of its questions carries that theme.</summary>
    private static bool TestMatchesTheme(Test t, string theme) =>
        t.Questions.Any(q => string.Equals(q.Theme, theme, StringComparison.CurrentCultureIgnoreCase));

    // ── Course-wide theme selector (replaces the lecture "by theme" filter) ──

    private UIElement BuildThemeSelector()
    {
        // Enlarged, leading theme selector (customer request 28-08-2026): the Тема scopes both the ready-test
        // list and generation, so it sits above the action cards and is sized to stand out — a larger label
        // and a taller, full-width dropdown.
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = AppStrings.ExamTheme, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary });

        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 16,
            MinHeight = 48,
            Padding = new Thickness(14, 8, 14, 8),
        };
        combo.Items.Add(new ComboBoxItem { Content = AppStrings.BankFilterAll, Tag = null });
        foreach (var s in _themes)
            combo.Items.Add(new ComboBoxItem { Content = s.Display, Tag = s.Value });
        // Set the selection before wiring the handler so re-rendering doesn't fire a spurious change.
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _selectedTheme) ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            _selectedTheme = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            // Drop a selected ready test the new theme hides.
            if (_selectedTestId is { } id && ReadyTests().All(t => t.TestId != id)) _selectedTestId = null;
            Render();
        };
        stack.Children.Add(combo);
        return stack;
    }

    /// <summary>The acronyms a lecture reinforces, resolved from its subsection through the taxonomy.</summary>
    private static HashSet<string> LectureAcronyms(QuickTestContext context)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(context.Subsection))
        {
            var key = Taxonomy.SubtopicKeyOf(context.Subsection!);
            foreach (var e in Taxonomy.Shared.ForSubtopic(key)) set.Add(e.Acronym);
        }
        return set;
    }

    /// <summary>True when the lecture gives us anything to filter by — a taxonomy subsection (preferred)
    /// or a legacy free-text theme.</summary>
    private bool HasLectureSignal => _lectureAcronyms.Count > 0 || !string.IsNullOrWhiteSpace(_context.Theme);

    /// <summary>A question belongs to this lecture when it carries one of the subsection's acronyms
    /// (precise), or — as a fallback for un-tagged banks — its free-text theme matches.</summary>
    private bool QuestionMatchesLecture(TestQuestion q) =>
        (_lectureAcronyms.Count > 0 && q.AcronymList.Any(_lectureAcronyms.Contains))
        || (_context.Theme is { } th && string.Equals(q.Theme, th, StringComparison.CurrentCultureIgnoreCase));

    private bool TestMatchesLecture(Test t) => t.Questions.Any(QuestionMatchesLecture);

    private UIElement BuildReadyTestsHeader()
    {
        var stack = new StackPanel { Spacing = 8 };

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

        // Filter tags (All / By theme) — only when the lecture gives us something to filter by.
        if (HasLectureSignal)
        {
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            tags.Children.Add(FilterTag("all", AppStrings.QuickFilterAll));
            tags.Children.Add(FilterTag("bytheme", AppStrings.QuickFilterByTheme));
            stack.Children.Add(tags);
        }

        return stack;
    }

    private UIElement BuildReadyTestsList()
    {
        var stack = new StackPanel { Spacing = 6 };

        var tests = ReadyTests();

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

        if (TestMatchesLecture(t))
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
        var start = PrimaryButton(_courseMode ? _startLabel ?? AppStrings.QuickStart : AppStrings.QuickStart);
        start.HorizontalAlignment = HorizontalAlignment.Stretch;
        start.Click += (_, _) => OnStart();

        // Course mode with no back label → a single full-width Start.
        var backLabel = _courseMode ? _backLabel : AppStrings.QuickBackToLecture;
        if (backLabel is null)
        {
            start.Margin = new Thickness(0, 4, 0, 0);
            return start;
        }

        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var back = new Button { Content = backLabel, HorizontalAlignment = HorizontalAlignment.Stretch };
        back.Click += (_, _) => BackToLectureRequested?.Invoke();
        Grid.SetColumn(back, 0);
        grid.Children.Add(back);

        Grid.SetColumn(start, 1);
        grid.Children.Add(start);
        return grid;
    }

    private void OnStart()
    {
        if (_appVm is null) return;

        // Group-configure mode hands the host a per-participant factory instead of a single test.
        if (_groupConfigure)
        {
            if (BuildGroupConfig() is { } config) GroupSessionRequested?.Invoke(config);
            return;
        }

        if (_action == "ready")
        {
            var test = _selectedTestId is { } id ? _appVm.TestRepository.Test(id) : null;
            if (test is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrNoTest); return; }
            // Course mode hands off immediately (the host swaps the view), so no "started" toast.
            if (!_courseMode) ShowToast("🚀", AppStrings.QuickStartedTitle, AppStrings.QuickStartedDescFormat(test.Title));
            TestStartRequested?.Invoke(test);
            return;
        }

        var generated = GenerateTest();
        if (generated is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrEmpty); return; }
        if (!_courseMode) ShowToast("🚀", AppStrings.QuickStartedTitle, AppStrings.QuickStartedDescFormat(generated.Title));
        TestStartRequested?.Invoke(generated);
    }

    /// <summary>Builds an ephemeral test from the bank, filtered to the lecture's theme + selected types,
    /// softly honouring the chosen difficulty. Returns null when no question matches.</summary>
    private Test? GenerateTest()
    {
        if (_appVm is null) return null;
        var mixed = _genTypes.Count == 0 || _genTypes.Contains("mixed");
        var types = new HashSet<string>(_genTypes);

        // Course mode scopes by the selected theme (null = all course themes); lecture mode by the
        // lecture's acronym/theme signal (no signal ⇒ everything is in scope).
        Func<TestQuestion, bool> scopeMatch = _courseMode
            ? q => _selectedTheme is null || string.Equals(q.Theme, _selectedTheme, StringComparison.CurrentCultureIgnoreCase)
            : q => !HasLectureSignal || QuestionMatchesLecture(q);

        var title = _courseMode
            ? string.Join(" · ", new[] { _courseTitle, _selectedTheme }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : $"{AppStrings.QuickTitle} · {SubtopicLabel()}".Trim();

        // Lecture mode falls back to the whole bank when the topic is too narrow (no tagged questions yet)
        // so a quick test can still be produced; course mode keeps the theme scope.
        return BuildTest(_appVm.QuestionBank.Questions, q => TypeMatch(q, types, mixed), scopeMatch,
            allowFallback: !_courseMode && HasLectureSignal, DifficultyValue(), _genCount, _genTime, title);
    }

    /// <summary>True when a question matches the selected generator test-types («mixed» ⇒ everything).</summary>
    private static bool TypeMatch(TestQuestion q, IReadOnlyCollection<string> types, bool mixed) => mixed ||
        (types.Contains("questions") && !q.IsAssembly && q.Stimulus is QuestionStimulus.Text or QuestionStimulus.Ecg) ||
        (types.Contains("image") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Image) ||
        (types.Contains("detect") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Ecg) ||
        (types.Contains("assemble") && q.IsAssembly) ||
        (types.Contains("case") && !q.IsAssembly && q.Stimulus == QuestionStimulus.Text);

    /// <summary>
    /// The pure core of test generation, shared by the Individual flow and the Group per-participant
    /// factory: filter the bank by type + scope (with an optional whole-bank fallback), softly prefer the
    /// chosen difficulty, shuffle, take <paramref name="count"/>, and renumber / re-id. A fresh
    /// <see cref="Random"/> per call makes each draw independent (so Group students get different sets).
    /// Returns null when nothing matches.
    /// </summary>
    private static Test? BuildTest(
        IReadOnlyList<TestQuestion> bank,
        Func<TestQuestion, bool> typeMatch,
        Func<TestQuestion, bool> scopeMatch,
        bool allowFallback,
        QuestionDifficulty? difficulty,
        int count,
        int timeMinutes,
        string title)
    {
        var candidates = bank.Where(q => typeMatch(q) && scopeMatch(q)).ToList();
        if (candidates.Count == 0 && allowFallback)
            candidates = bank.Where(typeMatch).ToList();
        if (candidates.Count == 0) return null;

        // Difficulty is a soft preference: matching questions first, then the rest.
        var pool = candidates;
        if (difficulty is { } diff)
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
        var chosen = ordered.Take(count).ToList();
        var perQuestion = (int)Math.Round(timeMinutes * 60.0 / chosen.Count);
        var questions = chosen.Select((q, i) => q with { Id = TestConstructorViewModel.NewId(), Number = i + 1 }).ToList();

        if (string.IsNullOrWhiteSpace(title)) title = AppStrings.QuickTitle;
        return new Test(TestConstructorViewModel.NewId(), title, questions, perQuestion);
    }

    /// <summary>
    /// Builds a <see cref="GroupTestConfig"/> from the current setup (group-configure mode). «Ready test»
    /// hands every participant a copy of the chosen authored test; «generate» draws a fresh, individually
    /// randomized test per participant honouring the selected types / count / time / difficulty / theme.
    /// Returns null (surfacing a toast) when nothing can be produced. The generate factory captures a
    /// snapshot of the parameters so it is self-contained and safe to call off the UI thread.
    /// </summary>
    private GroupTestConfig? BuildGroupConfig()
    {
        if (_appVm is null) return null;

        if (_action == "ready")
        {
            var test = _selectedTestId is { } id ? _appVm.TestRepository.Test(id) : null;
            if (test is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrNoTest); return null; }
            // Every participant takes the same authored test (read-only ⇒ safe to share the instance).
            return new GroupTestConfig(() => test);
        }

        var bank = _appVm.QuestionBank.Questions.ToList();
        var types = new HashSet<string>(_genTypes);
        var mixed = types.Count == 0 || types.Contains("mixed");
        var theme = _selectedTheme;
        var count = _genCount;
        var time = _genTime;
        var difficulty = DifficultyValue();
        var title = string.Join(" · ", new[] { _courseTitle, theme }.Where(s => !string.IsNullOrWhiteSpace(s)));

        Test? Factory() => BuildTest(
            bank,
            q => TypeMatch(q, types, mixed),
            q => theme is null || string.Equals(q.Theme, theme, StringComparison.CurrentCultureIgnoreCase),
            allowFallback: false, difficulty, count, time, title);

        // Validate once up front: an empty draw here means an empty draw for every student.
        if (Factory() is null) { ShowToast("⚠️", AppStrings.CommonCancel, AppStrings.QuickErrEmpty); return null; }
        return new GroupTestConfig(Factory);
    }

    private QuestionDifficulty? DifficultyValue() => _genDifficulty switch
    {
        "easy" => QuestionDifficulty.Easy,
        "hard" => QuestionDifficulty.Hard,
        "medium" => QuestionDifficulty.Medium,
        _ => null, // mixed
    };

    // ── Welcome banner ──────────────────────────────────────────────────────

    /// <summary>The one-time greeting, rendered in the card's normal flow (not a floating overlay) so it
    /// occupies its own space and never covers other elements. Styled as an accent-tinted hint card with
    /// a ✕ to dismiss it (a DispatcherQueueTimer does not tick inside the ContentDialog's modal loop, so
    /// auto-dismiss is unreliable here — the user reclaims the space by closing the banner instead).</summary>
    private UIElement BuildWelcomeBanner()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = $"👋 {AppStrings.QuickWelcomeTitle}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = AppTheme.TextPrimary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.QuickWelcomeDesc,
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var close = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12, Foreground = AppTheme.TextSecondary },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => { _showWelcome = false; Render(); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        return new Border
        {
            Child = grid,
            Background = AppTheme.AppAccentSoftBackground,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 12, 10),
        };
    }

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
