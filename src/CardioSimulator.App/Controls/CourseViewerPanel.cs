using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.Screens;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The Teaching screen's course panel: hosts the lecture <see cref="LectureWebView"/> for the
/// selected course (or a placeholder when none is loaded). A monitor button in the top bar pops the
/// <see cref="MonitorViewerOverlay"/> over it; an inline <c>&lt;ecg&gt;</c> embed can do the same
/// with a specific rhythm. The course/lecture selectors live in the top panel's
/// <see cref="TeachingControlPanel"/>. (In "All rhythms" mode the monitor is the main view, so this
/// panel isn't shown then.)
/// </summary>
public sealed class CourseViewerPanel : UserControl
{
    private static readonly string GlyphMonitor = char.ConvertFromUtf32(0xE95E); // "Health" heart/pulse glyph

    private readonly LectureWebView _web = new();
    private readonly TextBlock _placeholder = new()
    {
        Text = "Select a lecture",
        Foreground = Theming.AppTheme.TextSecondary,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _loadingLabel = new()
    {
        Text = AppStrings.LectureLoading,
        Foreground = Theming.AppTheme.TextSecondary,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    // Loading indicator shown while a lecture is being read/rendered (see UpdateContentArea).
    private readonly ProgressRing _loadingRing = new() { IsActive = false, Width = 40, Height = 40 };
    private readonly StackPanel _loadingPanel;
    private bool _loadingActive;

    private AppViewModel? _appVm;
    private CourseViewerViewModel? _viewer;
    private string? _selectedCourseId;

    /// <summary>Raised when the monitor should open. The payload is null for the top-bar button, or
    /// carries the embed's pathology/leads/scheme when triggered from an <c>&lt;ecg&gt;</c> button.</summary>
    public event EventHandler<EcgMonitorRequest?>? OpenMonitorRequested;

    private readonly Grid _topBar;

    public CourseViewerPanel()
    {
        Background = Theming.AppTheme.PageBackground;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: just the monitor button (right-aligned). It lives here rather than floated over
        // the content because the LectureWebView is a native airspace surface that renders above
        // XAML siblings; a button floated over the web region would be hidden behind it.
        _topBar = new Grid { Height = 56, Padding = new Thickness(16, 0, 8, 0), Background = Theming.AppTheme.PanelBackground };

        Loaded += (_, _) => Theming.AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;

        // End-of-lecture entry points: the self-assessment Testing flow, or the graded Examination.
        var endOfLectureButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var takeTestButton = new Button { Content = AppStrings.TeachingTakeTest };
        takeTestButton.Click += (_, _) => OpenQuickTest();
        endOfLectureButtons.Children.Add(takeTestButton);

        var takeExamButton = new Button { Content = AppStrings.TeachingTakeExam };
        takeExamButton.Click += (_, _) => SwitchToMode(OperatingMode.Examination);
        endOfLectureButtons.Children.Add(takeExamButton);

        _topBar.Children.Add(endOfLectureButtons);

        var monitorButton = new Button
        {
            Content = new FontIcon { Glyph = GlyphMonitor },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        monitorButton.Click += (_, _) => OpenMonitorRequested?.Invoke(this, null);
        _topBar.Children.Add(monitorButton);

        // An <ecg> embed's inline "open on monitor" button → open the monitor with that rhythm.
        _web.EcgOpenMonitorRequested += req => OpenMonitorRequested?.Invoke(this, req);
        // Loading lifecycle: opening a lecture reads/parses HTML, resolves inline ECGs and lays out
        // KaTeX before anything paints, so show a spinner over that gap (previously the view just sat
        // blank and felt frozen).
        _web.LoadingStarted += OnLectureLoadingStarted;
        _web.LoadingCompleted += OnLectureLoadingCompleted;
        Grid.SetRow(_topBar, 0);
        root.Children.Add(_topBar);

        // Content: the lecture web view, the placeholder, or the loading indicator (exactly one
        // visible). The lecture selector lives in the top panel.
        var content = new Grid();
        content.Children.Add(_web);
        content.Children.Add(_placeholder);

        // The LectureWebView is a native airspace surface that renders above its XAML siblings, so a
        // spinner floated on top of it would be hidden. Instead UpdateContentArea collapses the web
        // view while loading and shows this centered spinner in its place.
        _loadingPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Visibility = Visibility.Collapsed,
        };
        _loadingPanel.Children.Add(_loadingRing);
        _loadingPanel.Children.Add(_loadingLabel);
        content.Children.Add(_loadingPanel);

        _web.Visibility = Visibility.Collapsed;

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        Content = root;
    }

    private async void SwitchToMode(OperatingMode mode)
    {
        if (_appVm is null) return;
        var target = _appVm.OperatingModes.FirstOrDefault(m => m.Id == mode)
                     ?? new OperatingModeModel(mode);
        await _appVm.RequestOperatingModeAsync(target);
    }

    /// <summary>
    /// Opens the post-lecture Quick Test launcher (<see cref="QuickTestScreen"/>) over the lecture in a
    /// dialog, seeded with the current course/lecture context. Choosing a ready or generated test queues
    /// it (<see cref="AppViewModel.PendingTest"/>) and switches to Testing mode, which runs it directly;
    /// "back to lecture" just closes the dialog.
    /// </summary>
    private async void OpenQuickTest()
    {
        if (_appVm is null || _viewer is null) return;

        var quick = new QuickTestScreen();
        var dialog = new ContentDialog
        {
            Content = quick,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        // Widen past the default ContentDialog max so the launcher card (max 820) isn't clipped.
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        quick.BackToLectureRequested += () => dialog.Hide();
        quick.TestStartRequested += test =>
        {
            _appVm.PendingTest = test;
            dialog.Hide();
            SwitchToMode(OperatingMode.Testing);
        };
        quick.Initialize(_appVm, BuildQuickContext());

        void OnThemeChanged() => dialog.RequestedTheme = Theming.AppTheme.Current;
        Theming.AppTheme.Changed += OnThemeChanged;
        dialog.Closed += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;

        await dialog.ShowAsync();
    }

    /// <summary>Builds the launcher context from the open course/lecture: the lecture's Тема is the
    /// "section", the lecture itself the "subtopic". Section progress isn't tracked in the lecture flow
    /// (hidden), and there is no lecture→theme mapping yet, so generation draws from the whole bank.</summary>
    private QuickTestContext BuildQuickContext()
    {
        var course = _viewer?.SelectedCourse;
        var lecture = _viewer?.SelectedLecture;
        var courseName = course is not null ? Display(course.NameRu, course.TitleEn, course.Id) : string.Empty;
        var lectureTitle = lecture is not null ? Display(lecture.NameRu, lecture.TitleEn, lecture.Id) : string.Empty;

        var topic = lecture?.Topic is { } topicId ? course?.Topics.FirstOrDefault(t => t.Id == topicId) : null;
        var sectionLabel = topic is not null ? Display(topic.NameRu, topic.TitleEn, topic.Id) : courseName;

        // Prefer the lecture's own taxonomy subsection; fall back to its Тема's. Lets the launcher pull
        // exactly the questions that assess this material (via the acronym taxonomy) rather than the
        // whole bank. Null on legacy/un-mapped courses — the launcher then draws from everything.
        var subsection = lecture?.Subsection ?? topic?.Subsection;

        return new QuickTestContext(
            SectionLabel: sectionLabel,
            SubtopicId: subsection ?? string.Empty,
            SubtopicTitle: lectureTitle,
            SectionName: courseName,
            SectionProgressPercent: -1,
            Theme: null,
            Subsection: subsection);
    }

    private string Display(string? nameRu, string titleEn, string id) =>
        (_appVm!.SelectedLanguage == CardioSimulator.Core.Domain.Language.RU && !string.IsNullOrWhiteSpace(nameRu) ? nameRu
            : !string.IsNullOrWhiteSpace(titleEn) ? titleEn
            : nameRu) ?? id;

    public void Bind(AppViewModel appVm, CourseViewerViewModel viewer)
    {
        _appVm = appVm;
        _viewer = viewer;
        viewer.PropertyChanged += OnViewerChanged;
        appVm.PropertyChanged += OnAppChanged;
        SyncSelectedCourse();
        UpdateContentArea();
    }

    private void SyncSelectedCourse()
    {
        if (_appVm is null) return;
        var newId = _appVm.SelectedCourseId;
        if (_selectedCourseId == newId) return;
        _selectedCourseId = newId;
        if (_selectedCourseId is null)
        {
            // "All rhythms" — no course to read; clear any lecture so the viewer isn't left stale.
            _viewer?.Clear();
            return;
        }
        _viewer?.SelectCourse(_selectedCourseId);
        SelectFirstLectureIfNone();
    }

    /// <summary>
    /// On course selection, default to the course's first lecture so a lecture shows immediately —
    /// unless one is already selected (the user can still switch via the top-panel dropdown).
    /// </summary>
    private void SelectFirstLectureIfNone()
    {
        if (_appVm is null || _viewer is null || _viewer.SelectedLecture is not null) return;
        // FirstContentItemId covers both a Подтема and a leaf Тема (a course may have only leaf Темы).
        if (_viewer.SelectedCourse?.FirstContentItemId() is { } firstId)
            _viewer.SelectLecture(firstId, _appVm.SelectedLanguage.Tag());
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedCourseId))
        {
            SyncSelectedCourse();
            UpdateContentArea();
        }
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CourseViewerViewModel.LectureContent)) return;
        if (_viewer?.LectureContent is not null && _appVm is not null)
            _web.SetLecture(
                _viewer.LectureContent,
                EcgTraceResolver.ForRepository(_appVm.Repository),
                monitorButtonLabel: AppStrings.EcgOpenMonitor);
        UpdateContentArea();
    }

    private void OnLectureLoadingStarted()
    {
        _loadingActive = true;
        UpdateContentArea();
    }

    private void OnLectureLoadingCompleted()
    {
        _loadingActive = false;
        UpdateContentArea();
    }

    /// <summary>Shows the loading spinner while a lecture renders, then the lecture web view once it
    /// has painted, or the placeholder when no lecture is selected.</summary>
    private void UpdateContentArea()
    {
        var hasLecture = _viewer?.LectureContent is not null;
        _loadingRing.IsActive = _loadingActive;
        _loadingPanel.Visibility = _loadingActive ? Visibility.Visible : Visibility.Collapsed;
        // Collapse the native web surface while loading so the spinner (which it would otherwise
        // hide) shows; reveal it once the lecture has painted.
        _web.Visibility = hasLecture && !_loadingActive ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = !hasLecture && !_loadingActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Re-syncs the current course selection from the top panel (called when re-shown).</summary>
    public void Refresh()
    {
        SyncSelectedCourse();
        UpdateContentArea();
    }

    private void OnThemeChanged()
    {
        Background = Theming.AppTheme.PageBackground;
        _topBar.Background = Theming.AppTheme.PanelBackground;
        _placeholder.Foreground = Theming.AppTheme.TextSecondary;
        _loadingLabel.Foreground = Theming.AppTheme.TextSecondary;
        if (_viewer?.LectureContent is not null && _appVm is not null)
        {
            _web.SetLecture(
                _viewer.LectureContent,
                EcgTraceResolver.ForRepository(_appVm.Repository),
                monitorButtonLabel: AppStrings.EcgOpenMonitor);
        }
    }
}
