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
/// Learning Scale («Шкала обучения») dashboard — a native WinUI port of the HTML prototype. Shows the
/// student's course map (expandable sections + subtopics), an AI-style adaptive plan (Leitner-bucketed
/// tasks the student solves to raise mastery), a difficulty slider, a per-section progress histogram,
/// and header stats. All state + logic lives in <see cref="LearningScaleViewModel"/>; the whole page
/// re-renders on <see cref="LearningScaleViewModel.StateChanged"/>, on theme change, and on language
/// change, so it always matches the active theme/locale. Section/subtopic names are course content and
/// stay in the source language; the chrome is localized via <see cref="AppStrings"/>.
/// </summary>
public sealed class LearningScaleScreen : UserControl
{
    // Data-viz semantic colours (kept constant across themes so the bands read the same everywhere).
    private static readonly Color Green = Color.FromArgb(0xFF, 0x1A, 0x9A, 0x82);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xD6, 0xA8, 0x4A);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xD6, 0x6A, 0x6A);

    /// <summary>Neutral tone for "not started" (no graded attempts yet) sections/subtopics, so the
    /// absence of a measurement never reads as a red "critical".</summary>
    private static readonly Color Slate = Color.FromArgb(0xFF, 0x8A, 0x94, 0xA6);

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16, 12, 16, 16),
    };

    // Transient toast (bottom-right overlay).
    private readonly Border _toast;
    private readonly TextBlock _toastTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _toastDesc = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private DispatcherQueueTimer? _toastTimer;

    // Left drawer for student selection
    private readonly Border _drawerPanelHost;
    private readonly StackPanel _studentListStack = new() { Spacing = 4 };
    private readonly Border _drawerHandleBg;
    private readonly FontIcon _drawerHandleIcon;
    private bool _drawerExpanded;

    private LearningScaleViewModel? _vm;

    /// <summary>Launches a block's key test in the graded Testing section for a student (A3, customer 28-08).
    /// Set by the host (MainScreen), which owns mode switching; null = launching not wired.</summary>
    public Action<Test, Student?>? LaunchTest { get; set; }

    private readonly HashSet<int> _expanded = new();
    private double _difficulty = 48;
    private bool _welcomed;

    // Live-updated slider readouts (kept so ValueChanged can refresh them without a full re-render).
    private TextBlock? _difficultyValue;
    private TextBlock? _streakLabel;
    private TextBlock? _saveStatus;

    public LearningScaleScreen()
    {
        _toastDesc.Foreground = AppTheme.TextSecondary;
        var toastStack = new StackPanel();
        toastStack.Children.Add(_toastTitle);
        toastStack.Children.Add(_toastDesc);
        _toast = new Border
        {
            Child = toastStack,
            Background = AppTheme.AppCardBackground,
            BorderBrush = new SolidColorBrush(Green),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 20, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 24),
            MaxWidth = 380,
            Visibility = Visibility.Collapsed,
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var drawerHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(12, 12, 12, 8),
        };
        drawerHeader.Children.Add(new TextBlock { Text = "👥", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        drawerHeader.Children.Add(new TextBlock
        {
            Text = AppStrings.ModeName(OperatingMode.Students),
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var drawerStack = new StackPanel { Spacing = 6 };
        drawerStack.Children.Add(drawerHeader);
        drawerStack.Children.Add(Hairline());
        drawerStack.Children.Add(_studentListStack);

        _drawerPanelHost = new Border
        {
            Width = 260,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                Content = drawerStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(8, 0, 8, 12),
            },
            Visibility = Visibility.Collapsed,
        };

        _drawerHandleIcon = new FontIcon
        {
            Glyph = char.ConvertFromUtf32(0xE76C), // chevron pointing right
            FontSize = 14,
            Foreground = AppTheme.TextPrimary,
        };

        _drawerHandleBg = new Border
        {
            Width = 24,
            Height = 64,
            Background = AppTheme.AppSubtleFill,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 16, 16, 0),
            Child = _drawerHandleIcon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _drawerHandleBg.Tapped += (_, _) => ToggleDrawer();

        var drawerContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        drawerContainer.Children.Add(_drawerPanelHost);
        drawerContainer.Children.Add(_drawerHandleBg);

        Grid.SetColumn(drawerContainer, 0);
        Grid.SetColumn(_scroll, 1);
        Grid.SetColumn(_toast, 1);

        _root.Children.Add(drawerContainer);
        _root.Children.Add(_scroll);
        _root.Children.Add(_toast);
        Content = _root;

        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += OnUnloaded;
    }

    public void Initialize(LearningScaleViewModel vm)
    {
        _vm = vm;
        vm.StateChanged += Render;
        Render();

        if (!_welcomed)
        {
            _welcomed = true;
            ShowToast("👋", AppStrings.LsToastWelcomeTitle, AppStrings.LsToastWelcomeDesc);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppTheme.Changed -= OnThemeChanged;
        if (_vm is not null) _vm.StateChanged -= Render;
        _toastTimer?.Stop();
    }

    private void OnThemeChanged()
    {
        _toast.Background = AppTheme.AppCardBackground;
        _toastDesc.Foreground = AppTheme.TextSecondary;
        _drawerPanelHost.Background = AppTheme.AppCardBackground;
        _drawerPanelHost.BorderBrush = AppTheme.AppCardBorder;
        _drawerHandleBg.Background = AppTheme.AppSubtleFill;
        _drawerHandleBg.BorderBrush = AppTheme.AppCardBorder;
        _drawerHandleIcon.Foreground = AppTheme.TextPrimary;
        Render();
    }

    private void ToggleDrawer()
    {
        _drawerExpanded = !_drawerExpanded;
        _drawerPanelHost.Visibility = _drawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        _drawerHandleIcon.Glyph = _drawerExpanded ? char.ConvertFromUtf32(0xE76B) : char.ConvertFromUtf32(0xE76C);
    }

    private void RebuildDrawerList()
    {
        _studentListStack.Children.Clear();
        if (_vm is null) return;

        var roster = _vm.Roster;
        var selected = _vm.SelectedStudent;

        if (roster.Count == 0)
        {
            _studentListStack.Children.Add(new TextBlock
            {
                Text = AppStrings.LsDemoUserName,
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(8, 4, 8, 4),
            });
            return;
        }

        // Option 0: All Students (Cohort view)
        _studentListStack.Children.Add(BuildStudentDrawerItem(
            name: AppStrings.LsStudentAll,
            group: null,
            avatar: "👥",
            isSelected: selected is null,
            onClick: () => _vm.SelectStudent(null)));

        // Roster items
        foreach (var student in roster)
        {
            var s = student;
            var initial = s.FullName.Length > 0 ? s.FullName[..1].ToUpperInvariant() : "•";
            var isSel = ReferenceEquals(selected, s) ||
                (selected is not null && string.Equals(selected.Id, s.Id, StringComparison.Ordinal));

            _studentListStack.Children.Add(BuildStudentDrawerItem(
                name: s.FullName,
                group: string.IsNullOrWhiteSpace(s.Group) ? null : s.Group,
                avatar: initial,
                isSelected: isSel,
                onClick: () => _vm.SelectStudent(s)));
        }
    }

    private UIElement BuildStudentDrawerItem(
        string name,
        string? group,
        string avatar,
        bool isSelected,
        Action onClick)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(4, 2, 4, 2),
        };

        content.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = isSelected ? new SolidColorBrush(Green) : AppTheme.AppSubtleFill,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = avatar,
                FontSize = 12,
                Foreground = isSelected ? new SolidColorBrush(Colors.White) : AppTheme.TextPrimary,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isSelected ? AppTheme.Accent : AppTheme.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
        });

        if (!string.IsNullOrEmpty(group))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = group,
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180,
            });
        }

        content.Children.Add(textStack);

        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = isSelected ? SoftBrush(Green) : AppTheme.AppCardBackground,
            BorderBrush = isSelected ? new SolidColorBrush(Green) : AppTheme.AppCardBorder,
            BorderThickness = isSelected ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(6, 4, 8, 4),
            Margin = new Thickness(0, 1, 0, 1),
        };

        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ── Page ────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (_vm is null) return;

        RebuildDrawerList();

        // No course package loaded (or it has no content) → a prompt instead of an empty dashboard.
        if (_vm.Sections.Count == 0)
        {
            _scroll.Content = BuildNoCourse();
            return;
        }

        var page = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(BuildHeader());
        page.Children.Add(BuildGlobalProgress());
        page.Children.Add(BuildMainGrid());
        page.Children.Add(BuildChart());
        page.Children.Add(BuildFooter());

        _scroll.Content = page;
    }

    // ── Header (brand + user) ────────────────────────────────────────────────

    private UIElement BuildHeader()
    {
        var stack = new StackPanel { Spacing = 12 };

        // Row 1: brand ····· user chip.
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock { Text = "📈", FontSize = 26, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock
        {
            Text = AppStrings.ModeName(OperatingMode.LearningScale),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = AppTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(brand, 0);
        row1.Children.Add(brand);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        // Course dropdown (A1, customer 28-08) sits where the "Уровень" (level) badge used to be — the
        // level badge was removed per the same feedback (A2). BuildLevelBadge()/LsLevelBadge deleted.
        if (BuildCourseSelector() is { } courseSelector) right.Children.Add(courseSelector);
        right.Children.Add(BuildUserChip());
        Grid.SetColumn(right, 1);
        row1.Children.Add(right);
        stack.Children.Add(row1);

        return stack;
    }

    /// <summary>The course dropdown (A1): every loaded course, rebuilding the map on change. A single loaded
    /// course shows as a static label; no courses returns null (header omits it).</summary>
    private UIElement? BuildCourseSelector()
    {
        var courses = _vm!.Courses;
        if (courses.Count == 0) return null;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "📚", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.CourseSelectorTitle, FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center });

        if (courses.Count == 1)
        {
            stack.Children.Add(new TextBlock
            {
                Text = courses[0].Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        else
        {
            var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 200 };
            foreach (var c in courses) combo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _vm.SelectedCourseId) ?? combo.Items[0];
            combo.SelectionChanged += (_, _) => { if ((combo.SelectedItem as ComboBoxItem)?.Tag is string id) _vm!.SelectCourse(id); };
            stack.Children.Add(combo);
        }

        return new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(16),
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    private UIElement BuildUserChip()
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(6, 4, 12, 4),
        };
        var wrap = new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(16),
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            Child = chip,
        };

        var selected = _vm!.SelectedStudent;
        var hasRoster = _vm.Roster.Count > 0;

        // Avatar: the selected student's initial; a group glyph for the cohort aggregate; the demo
        // initial only when no students are registered at all.
        string avatarText;
        if (selected is not null)
            avatarText = selected.FullName.Length > 0 ? selected.FullName[..1].ToUpperInvariant() : "•";
        else
            avatarText = hasRoster ? "👥" : (AppStrings.LsDemoUserName.Length > 0 ? AppStrings.LsDemoUserName[..1].ToUpperInvariant() : "•");

        chip.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Green),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = avatarText,
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        // No registered students → the static placeholder chip (also the standalone demo experience).
        if (!hasRoster)
        {
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            details.Children.Add(new TextBlock { Text = AppStrings.LsDemoUserName, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = AppTheme.TextPrimary });
            details.Children.Add(new TextBlock { Text = AppStrings.LsDemoUserGroup, FontSize = 11, Foreground = AppTheme.TextSecondary });
            chip.Children.Add(details);
            return wrap;
        }

        // Title of student (replaces ComboBox)
        var studentDetails = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        if (selected is not null)
        {
            studentDetails.Children.Add(new TextBlock
            {
                Text = selected.FullName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
            });
            if (!string.IsNullOrWhiteSpace(selected.Group))
            {
                studentDetails.Children.Add(new TextBlock
                {
                    Text = selected.Group,
                    FontSize = 11,
                    Foreground = AppTheme.TextSecondary,
                });
            }
        }
        else
        {
            studentDetails.Children.Add(new TextBlock
            {
                Text = AppStrings.LsStudentAll,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
            });
        }

        chip.Children.Add(studentDetails);
        return wrap;
    }

    // ── Global progress ──────────────────────────────────────────────────────

    private UIElement BuildGlobalProgress()
    {
        var percent = _vm!.GlobalProgressPercent;
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = AppStrings.LsGlobalProgress, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new Border
        {
            Background = new SolidColorBrush(Green),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 1, 10, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = AppStrings.LsGlobalBadge, FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
        });
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var track = ProgressTrack(percent, new SolidColorBrush(Green), 10);
        track.Margin = new Thickness(16, 0, 16, 0);
        track.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var pct = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        pct.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = percent.ToString() });
        pct.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "%", Foreground = AppTheme.TextSecondary, FontSize = 14 });
        Grid.SetColumn(pct, 2);
        grid.Children.Add(pct);

        return Card(grid, useSubtleFill: true, padding: new Thickness(20, 12, 20, 12), radius: 16);
    }

    // ── Main grid: sections map (left) + adaptive plan (right) ────────────────

    private UIElement BuildMainGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        var map = BuildSectionsMap();
        Grid.SetColumn((FrameworkElement)map, 0);
        grid.Children.Add(map);

        var plan = BuildPlanPanel();
        Grid.SetColumn((FrameworkElement)plan, 2);
        grid.Children.Add(plan);

        return grid;
    }

    private UIElement BuildSectionsMap()
    {
        var stack = new StackPanel { Spacing = 6 };

        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = AppStrings.LsSectionsTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary };
        Grid.SetColumn(title, 0);
        titleRow.Children.Add(title);
        var updated = new TextBlock
        {
            Text = _vm!.HasInteracted ? AppStrings.LsUpdatedNow : AppStrings.LsUpdatedToday,
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(updated, 1);
        titleRow.Children.Add(updated);
        stack.Children.Add(titleRow);
        stack.Children.Add(Hairline());

        foreach (var section in _vm.Sections)
            stack.Children.Add(BuildSectionItem(section));

        stack.Children.Add(Hairline());
        stack.Children.Add(new TextBlock { Text = AppStrings.LsLegend, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsSectionsHint, FontSize = 11, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });

        return Card(stack, useSubtleFill: true);
    }

    private UIElement BuildSectionItem(LsSection section)
    {
        var (color, emoji) = section.HasData ? StatusVisual(section.Status) : (Slate, "○");
        var pctText = section.HasData ? $"{section.Progress}%" : "—";
        var expandable = section.Subtopics.Count > 0;
        var isOpen = expandable && _expanded.Contains(section.Id);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = section.Id.ToString(), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = new SolidColorBrush(Green), VerticalAlignment = VerticalAlignment.Center, MinWidth = 18 });
        left.Children.Add(new TextBlock { Text = section.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(Badge($"{emoji} {pctText}", color));
        // Only a grouping section (with subtopics) shows an expand chevron; a leaf Тема is a plain row.
        if (expandable)
            left.Children.Add(new TextBlock { Text = isOpen ? "▲" : "▼", FontSize = 11, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var rightSide = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var bar = ProgressTrack(section.Progress, new SolidColorBrush(color), 6);
        bar.Width = 80;
        bar.VerticalAlignment = VerticalAlignment.Center;
        rightSide.Children.Add(bar);
        rightSide.Children.Add(new TextBlock { Text = pctText, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, MinWidth = 34, TextAlignment = TextAlignment.Right });
        Grid.SetColumn(rightSide, 1);
        row.Children.Add(rightSide);

        var header = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = AppTheme.AppCardBackground,
            BorderBrush = new SolidColorBrush(WithAlpha(color, 0x66)),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 3, 0, 0),
        };
        var sid = section.Id;
        header.Click += (_, _) =>
        {
            if (!expandable) return; // a leaf section has nothing to expand
            if (!_expanded.Add(sid)) _expanded.Remove(sid);
            Render();
        };

        if (!isOpen) return header;

        // Expanded: the header, then the subtopics indented beneath it.
        var subs = new StackPanel { Spacing = 3, Margin = new Thickness(28, 3, 0, 0) };
        foreach (var sub in section.Subtopics)
            subs.Children.Add(BuildSubtopicItem(section, sub));

        var outer = new StackPanel();
        outer.Children.Add(header);
        outer.Children.Add(subs);
        return outer;
    }

    private UIElement BuildSubtopicItem(LsSection section, LsSubtopic sub)
    {
        var dot = !sub.HasData ? Slate : sub.Progress >= 70 ? Green : sub.Progress >= 40 ? Amber : Red;
        var subPctText = sub.HasData ? $"{sub.Progress}%" : "—";
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Prefix the name with the taxonomy code (e.g. "3.1") when the node is mapped; otherwise just
        // the name — never the internal node id.
        var label = string.IsNullOrEmpty(sub.Key) ? sub.Name : $"{sub.Key} {sub.Name}";
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(dot), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(left, 0);
        content.Children.Add(left);

        var pct = new TextBlock { Text = subPctText, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pct, 1);
        content.Children.Add(pct);

        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = AppTheme.AppSubtleFill,
            BorderBrush = new SolidColorBrush(WithAlpha(dot, 0x88)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6, 12, 6),
        };
        var sectionId = section.Id;
        var progress = sub.Progress;
        btn.Click += (_, _) => ShowToast("📖", label, AppStrings.LsToastSubtopicDescFormat(sectionId, progress));

        // A3 (customer 28-08): if this block has a key/«главный» test, add a «Сдать/Пересдать» button that
        // launches it in the graded Testing section for the selected student (and returns here on finish).
        var keyTest = sub.Key is { } key ? _vm!.PrimaryTestFor?.Invoke(key) : null;
        if (keyTest is null || LaunchTest is null) return btn;

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(btn, 0);
        row.Children.Add(btn);

        var hasStudent = _vm!.SelectedStudent is not null;
        var take = new Button
        {
            Content = sub.HasData ? AppStrings.LsBlockRetake : AppStrings.LsBlockTake,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = hasStudent,
        };
        ToolTipService.SetToolTip(take, hasStudent ? keyTest.Title : AppStrings.LsBlockPickStudent);
        var capturedTest = keyTest;
        take.Click += (_, _) => LaunchTest?.Invoke(capturedTest, _vm.SelectedStudent);
        Grid.SetColumn(take, 1);
        row.Children.Add(take);
        return row;
    }

    // ── Adaptive plan ─────────────────────────────────────────────────────────

    private UIElement BuildPlanPanel()
    {
        var stack = new StackPanel { Spacing = 10 };

        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var h = new TextBlock { Text = AppStrings.LsPlanTitle, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(h, 0);
        headerRow.Children.Add(h);
        var aiBadge = new Border
        {
            Background = SoftBrush(Green),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = AppStrings.LsAiBadge, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Green) },
        };
        Grid.SetColumn(aiBadge, 1);
        headerRow.Children.Add(aiBadge);
        stack.Children.Add(headerRow);

        var tasks = _vm!.GenerateTasks();
        if (tasks.Count == 0)
        {
            // Distinguish "you've cleared every recommendation" (real data) from "nothing assessed yet"
            // (fresh install) — the celebration would be misleading before any test has been graded.
            stack.Children.Add(_vm.IsRealData ? BuildAllDone() : BuildNoData());
        }
        else
        {
            // Customer priority order (28-08): next step → needs attention → in progress.
            AddTaskGroup(stack, AppStrings.LsGroupNext, tasks.Where(t => t.Type == PlanTaskType.Next));
            AddTaskGroup(stack, AppStrings.LsGroupCritical, tasks.Where(t => t.Type == PlanTaskType.Critical));
            AddTaskGroup(stack, AppStrings.LsGroupGrowth, tasks.Where(t => t.Type == PlanTaskType.Growth));
        }

        stack.Children.Add(BuildDifficultySlider());
        return Card(stack, useSubtleFill: true);
    }

    private void AddTaskGroup(StackPanel host, string label, IEnumerable<PlanTask> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var group = new StackPanel { Spacing = 5, Margin = new Thickness(0, 2, 0, 6) };
        group.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextSecondary, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var task in list)
            group.Children.Add(BuildTaskItem(task));
        host.Children.Add(group);
    }

    private UIElement BuildTaskItem(PlanTask task)
    {
        var color = TaskColor(task.Type);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = task.SubtopicName, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 1, 10, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = task.SectionName, FontSize = 10, Foreground = AppTheme.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis },
        });
        info.Children.Add(Badge(TaskBadgeText(task), color, small: true));
        Grid.SetColumn(info, 0);
        content.Children.Add(info);

        var hint = new TextBlock { Text = AppStrings.LsTaskClick, FontSize = 9, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 1);
        content.Children.Add(hint);

        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = AppTheme.AppCardBackground,
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 8, 14, 8),
        };
        btn.Click += async (_, _) => await OpenTaskAsync(task);
        return btn;
    }

    private UIElement BuildNoCourse()
    {
        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "📚", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsNoCourseTitle, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsNoCourseBody, FontSize = 14, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        return stack;
    }

    private UIElement BuildNoData()
    {
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 24, 0, 24) };
        stack.Children.Add(new TextBlock { Text = "📊", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsNoDataTitle, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsNoDataBody, FontSize = 13, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    private UIElement BuildAllDone()
    {
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 24, 0, 24) };
        stack.Children.Add(new TextBlock { Text = "🎉", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsAllDoneTitle, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsAllDoneBody, FontSize = 13, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = AppStrings.LsAllDoneHint, FontSize = 12, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        return stack;
    }

    private UIElement BuildDifficultySlider()
    {
        var stack = new StackPanel { Spacing = 4 };

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = AppStrings.LsDifficultyLabel, FontSize = 13, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var slider = new Slider
        {
            Minimum = 5,
            Maximum = 95,
            Value = _difficulty,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        _difficultyValue = new TextBlock { Text = $"{(int)Math.Round(_difficulty)}%", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_difficultyValue, 2);
        row.Children.Add(_difficultyValue);
        stack.Children.Add(row);

        var labels = new Grid();
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var easy = new TextBlock { Text = AppStrings.LsDifficultyEasy, FontSize = 10, Foreground = AppTheme.TextSecondary };
        Grid.SetColumn(easy, 0);
        labels.Children.Add(easy);
        _streakLabel = new TextBlock { Text = AppStrings.LsStreak, FontSize = 10, Foreground = new SolidColorBrush(Green), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        Grid.SetColumn(_streakLabel, 1);
        labels.Children.Add(_streakLabel);
        var hard = new TextBlock { Text = AppStrings.LsDifficultyHard, FontSize = 10, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(hard, 2);
        labels.Children.Add(hard);
        stack.Children.Add(labels);

        slider.ValueChanged += (_, e) =>
        {
            _difficulty = e.NewValue;
            var p = (int)Math.Round(e.NewValue);
            if (_difficultyValue is not null) _difficultyValue.Text = $"{p}%";
            if (_streakLabel is not null)
            {
                if (p > 70) { _streakLabel.Text = AppStrings.LsLevelHigh; _streakLabel.Foreground = new SolidColorBrush(Red); }
                else if (p > 40) { _streakLabel.Text = AppStrings.LsLevelMid; _streakLabel.Foreground = new SolidColorBrush(Amber); }
                else { _streakLabel.Text = AppStrings.LsLevelLow; _streakLabel.Foreground = new SolidColorBrush(Green); }
            }
        };

        return new Border
        {
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(0, 6, 0, 0),
            Child = stack,
        };
    }

    // ── Progress histogram ────────────────────────────────────────────────────

    private UIElement BuildChart()
    {
        var stack = new StackPanel { Spacing = 12 };

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = AppStrings.LsChartTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary };
        Grid.SetColumn(t, 0);
        headerRow.Children.Add(t);
        var sub = new TextBlock { Text = AppStrings.LsChartSubtitle, FontSize = 13, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(sub, 1);
        headerRow.Children.Add(sub);
        stack.Children.Add(headerRow);

        const double chartHeight = 130;
        var bars = new Grid { Height = chartHeight + 24 };
        var sections = _vm!.Sections;
        for (var i = 0; i < sections.Count; i++)
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var color = !s.HasData ? Slate : s.Progress >= 80 ? Green : s.Progress >= 40 ? Amber : Red;
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new TextBlock { Text = s.HasData ? $"{s.Progress}%" : "—", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) });
            col.Children.Add(new Border
            {
                Width = 40,
                Height = Math.Max(4, s.Progress / 100.0 * chartHeight),
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
            });
            col.Children.Add(new TextBlock { Text = AppStrings.LsSectionShortFormat(s.Id), FontSize = 10, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            Grid.SetColumn(col, i);
            bars.Children.Add(col);
        }
        stack.Children.Add(bars);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, HorizontalAlignment = HorizontalAlignment.Center };
        legend.Children.Add(LegendItem(Green, AppStrings.LsChartLegendMastered));
        legend.Children.Add(LegendItem(Amber, AppStrings.LsChartLegendProgress));
        legend.Children.Add(LegendItem(Red, AppStrings.LsChartLegendAttention));
        stack.Children.Add(Hairline());
        stack.Children.Add(legend);

        return Card(stack, useSubtleFill: true);
    }

    private UIElement LegendItem(Color color, string text)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        return s;
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private UIElement BuildFooter()
    {
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var a = new TextBlock { Text = AppStrings.LsFooterStats, FontSize = 12, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(a, 0);
        footer.Children.Add(a);
        var b = new TextBlock { Text = AppStrings.LsFooterAlgo, FontSize = 12, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(b, 1);
        footer.Children.Add(b);
        _saveStatus = new TextBlock { Text = AppStrings.LsFooterSaved, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Accent, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(_saveStatus, 2);
        footer.Children.Add(_saveStatus);

        return new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(4, 12, 4, 0),
            Child = footer,
        };
    }

    // ── Task detail dialog ─────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task OpenTaskAsync(PlanTask task)
    {
        if (_vm is null || _vm.IsCompleted(task.Id)) return;

        var body = new StackPanel { Spacing = 0, MinWidth = 380 };
        body.Children.Add(DetailRow(AppStrings.LsDetailSection, task.SectionName));
        body.Children.Add(DetailRow(AppStrings.LsDetailSubtopic, $"{task.SubtopicId} {task.SubtopicName}"));
        body.Children.Add(DetailRow(AppStrings.LsDetailType, TaskLabel(task.Type)));
        body.Children.Add(DetailRow(AppStrings.LsDetailProgress, $"{task.Progress}%"));
        body.Children.Add(DetailRow(AppStrings.LsDetailDifficulty, task.Type switch
        {
            PlanTaskType.Critical => "8/10",
            PlanTaskType.Growth => "6/10",
            _ => "3/10",
        }, last: true));

        var tree = new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock
            {
                Text = $"📖  {AppStrings.LsDetailSection} {task.SectionId} → {task.SectionName} → {task.SubtopicId} {task.SubtopicName}",
                FontSize = 13,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        body.Children.Add(tree);

        var dialog = new ContentDialog
        {
            Title = task.SubtopicName,
            Content = body,
            PrimaryButtonText = AppStrings.LsMarkDone,
            CloseButtonText = AppStrings.LsClose,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var newProgress = _vm.MarkDone(task.Id);
            if (newProgress is { } p)
            {
                FlashSaved();
                ShowToast("✅", AppStrings.LsToastSolvedTitle, AppStrings.LsToastSolvedDescFormat(task.SectionId, task.SectionName, p));
            }
        }
    }

    private UIElement DetailRow(string label, string value, bool last = false)
    {
        var grid = new Grid { Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 14, Foreground = AppTheme.TextSecondary };
        Grid.SetColumn(l, 0);
        grid.Children.Add(l);
        var v = new TextBlock { Text = value, FontSize = 14, Foreground = AppTheme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);

        if (last) return grid;
        var wrap = new StackPanel();
        wrap.Children.Add(grid);
        wrap.Children.Add(Hairline());
        return wrap;
    }

    // ── Toast ─────────────────────────────────────────────────────────────────

    private void ShowToast(string emoji, string title, string desc)
    {
        _toastTitle.Text = $"{emoji} {title}";
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

    private void FlashSaved()
    {
        if (_saveStatus is null) return;
        _saveStatus.Text = AppStrings.LsFooterSavedAtFormat(DateTime.Now.ToString("HH:mm:ss"));
    }

    // ── Small view helpers ─────────────────────────────────────────────────────

    /// <summary>A rounded card. <paramref name="useSubtleFill"/> picks the panel's inner fill vs. the
    /// plain card background.</summary>
    private static Border Card(UIElement child, bool useSubtleFill = false, Thickness? padding = null, double radius = 16) => new()
    {
        Child = child,
        Background = useSubtleFill ? AppTheme.AppSubtleFill : AppTheme.AppCardBackground,
        BorderBrush = AppTheme.AppCardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding ?? new Thickness(20, 16, 20, 16),
    };

    /// <summary>A proportional progress track: a rounded rail with the fill sized by a two-column
    /// (percent / remainder) grid, so it scales with the available width without measuring.</summary>
    private Border ProgressTrack(int percent, Brush fill, double height)
    {
        percent = Math.Clamp(percent, 0, 100);
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
        var fillBar = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumn(fillBar, 0);
        inner.Children.Add(fillBar);
        return new Border
        {
            Height = height,
            Background = AppTheme.AppCardBorder,
            CornerRadius = new CornerRadius(height / 2),
            Child = inner,
        };
    }

    private Border Badge(string text, Color color, bool small = false) => new()
    {
        Background = SoftBrush(color),
        CornerRadius = new CornerRadius(16),
        Padding = small ? new Thickness(9, 1, 9, 1) : new Thickness(10, 2, 10, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = small ? 10 : 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) },
    };

    private Border Hairline() => new()
    {
        Height = 1,
        Background = AppTheme.AppCardBorder,
        Margin = new Thickness(0, 8, 0, 8),
    };

    private static (Color color, string emoji) StatusVisual(SectionStatus status) => status switch
    {
        SectionStatus.Good => (Green, "🟢"),
        SectionStatus.Warning => (Amber, "🟠"),
        _ => (Red, "🔴"),
    };

    private static Color TaskColor(PlanTaskType type) => type switch
    {
        PlanTaskType.Next => Green,
        PlanTaskType.Critical => Red,
        PlanTaskType.Growth => Amber,
        _ => Green,
    };

    private static string TaskLabel(PlanTaskType type) => type switch
    {
        PlanTaskType.Next => AppStrings.LsLabelNext,
        PlanTaskType.Critical => AppStrings.LsLabelCritical,
        PlanTaskType.Growth => AppStrings.LsLabelGrowth,
        _ => AppStrings.LsLabelFix,
    };

    private static string TaskBadgeText(PlanTask task) => task.Type switch
    {
        PlanTaskType.Next => AppStrings.LsBadgeNext,
        PlanTaskType.Critical => AppStrings.LsBadgeErrorFormat((int)Math.Round(100.0 - task.Progress)),
        PlanTaskType.Growth => $"{task.Progress}%",
        _ => AppStrings.LsBadgeRepeat,
    };

    private static SolidColorBrush SoftBrush(Color color) => new(WithAlpha(color, 0x26));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
