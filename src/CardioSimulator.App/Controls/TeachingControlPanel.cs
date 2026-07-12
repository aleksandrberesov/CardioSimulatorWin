using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Teaching-mode top sub-panel: a course selector plus context-sensitive selectors, adapting to the
/// course's shape:
/// <list type="bullet">
/// <item>a course with <b>no Темы</b> (flat <c>Course → Lecture</c>) shows a plain lecture selector;</item>
/// <item>a course <b>with Темы</b> shows a flat Тема (topic) selector — picking a <b>group</b> Тема
/// reveals a second Подтема (subtopic) selector, while a <b>leaf</b> Тема opens its own lecture and
/// shows no second selector;</item>
/// <item>"All rhythms" hides the Тема selector and turns the second selector into a rhythm picker (the
/// monitor's rhythm), defaulting to the last-used rhythm, or the first if none.</item>
/// </list>
/// Port of the Android <c>TeachingControlPanel</c>.
/// </summary>
public sealed class TeachingControlPanel : UserControl
{
    private readonly Tab _courseTab = new();
    private readonly Tab _topicTab = new();
    private readonly Tab _itemTab = new();
    private AppViewModel? _appViewModel;
    private CourseViewerViewModel? _courseViewer;
    private RhythmViewModel? _rhythmViewModel;

    /// <summary>The Тема currently selected (a leaf or group topic id), or null when none is chosen.</summary>
    private string? _selectedTopicId;

    public TeachingControlPanel()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _courseTab.Margin = new Thickness(4, 0, 4, 0);
        _courseTab.MinWidth = 120;
        _courseTab.ShowChevron = true;
        _courseTab.Large = true;
        _courseTab.VerticalAlignment = VerticalAlignment.Center;
        _courseTab.Click += OnCourseClick;
        row.Children.Add(_courseTab);

        _topicTab.Margin = new Thickness(4, 0, 4, 0);
        _topicTab.MinWidth = 120;
        _topicTab.ShowChevron = true;
        _topicTab.Large = true;
        _topicTab.VerticalAlignment = VerticalAlignment.Center;
        _topicTab.Click += OnTopicClick;
        row.Children.Add(_topicTab);

        _itemTab.Margin = new Thickness(4, 0, 4, 0);
        _itemTab.MinWidth = 120;
        _itemTab.ShowChevron = true;
        _itemTab.Large = true;
        _itemTab.VerticalAlignment = VerticalAlignment.Center;
        _itemTab.Click += OnItemClick;
        row.Children.Add(_itemTab);

        Content = row;
        // The top bar replaces this sub-panel on every mode switch; drop our VM subscriptions when
        // removed so a stale panel doesn't keep reacting to app events (and touching UI off-thread).
        Unloaded += (_, _) => Detach();
    }

    private void Detach()
    {
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnAppChanged;
        if (_courseViewer is not null) _courseViewer.PropertyChanged -= OnCourseViewerChanged;
        if (_rhythmViewModel is not null) _rhythmViewModel.PropertyChanged -= OnRhythmChanged;
        _appViewModel = null;
        _courseViewer = null;
        _rhythmViewModel = null;
    }

    public void Bind(AppViewModel appViewModel, RhythmViewModel rhythmViewModel)
    {
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnAppChanged;
        if (_courseViewer is not null) _courseViewer.PropertyChanged -= OnCourseViewerChanged;
        if (_rhythmViewModel is not null) _rhythmViewModel.PropertyChanged -= OnRhythmChanged;
        _appViewModel = appViewModel;
        _courseViewer = appViewModel.CourseViewerViewModel;
        _rhythmViewModel = rhythmViewModel;
        _appViewModel.PropertyChanged += OnAppChanged;
        _courseViewer.PropertyChanged += OnCourseViewerChanged;
        _rhythmViewModel.PropertyChanged += OnRhythmChanged;
        SyncTopicFromLecture();
        UpdateSelectors();
        if (IsAllRhythms) EnsureRhythmSelected();
    }

    /// <summary>True when no course is selected ("All rhythms"); the item selector then picks a
    /// rhythm instead of a subtopic, and the Тема selector is hidden.</summary>
    private bool IsAllRhythms => _appViewModel is not null && _appViewModel.SelectedCourseId is null;

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedCourseId)
            or nameof(AppViewModel.Courses)
            or nameof(AppViewModel.SelectedLanguage))
        {
            if (e.PropertyName == nameof(AppViewModel.SelectedCourseId)) SyncTopicFromLecture();
            UpdateSelectors();
            if (e.PropertyName == nameof(AppViewModel.SelectedCourseId) && IsAllRhythms)
                EnsureRhythmSelected();
        }
    }

    private void OnCourseViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The course viewer drives lecture state; the selectors here just reflect it.
        if (e.PropertyName is nameof(CourseViewerViewModel.SelectedLecture)
            or nameof(CourseViewerViewModel.SelectedCourse))
        {
            SyncTopicFromLecture();
            UpdateSelectors();
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RhythmViewModel.Rhythms)
            or nameof(RhythmViewModel.SelectedRhythm))
        {
            UpdateItemLabel();
            // Rhythms load asynchronously; ensure a default selection once they arrive.
            if (e.PropertyName == nameof(RhythmViewModel.Rhythms) && IsAllRhythms)
                EnsureRhythmSelected();
        }
    }

    /// <summary>Refreshes each selector's label and which selectors are visible for the current mode.</summary>
    private void UpdateSelectors()
    {
        // "All rhythms": course + rhythm. Flat course (no Темы): course + lecture picker.
        // Topic course: course + Тема, plus a Подтема picker for a group Тема (none for a leaf Тема).
        _topicTab.Visibility = !IsAllRhythms && HasTopics ? Visibility.Visible : Visibility.Collapsed;
        _itemTab.Visibility = IsAllRhythms || !HasTopics || IsGroupSelected ? Visibility.Visible : Visibility.Collapsed;
        UpdateCourseLabel();
        UpdateTopicLabel();
        UpdateItemLabel();
    }

    /// <summary>True when the selected course organizes its lectures into Темы (vs a flat lecture list).</summary>
    private bool HasTopics => _courseViewer?.SelectedCourse is { } course && course.Topics.Count > 0;

    private void UpdateCourseLabel()
    {
        if (_appViewModel is null) return;
        var selected = _appViewModel.SelectedCourseId is { } id
            ? _appViewModel.Courses.FirstOrDefault(c => c.Id == id)
            : null;
        _courseTab.Text = selected is null ? AppStrings.RhythmCourseFilterAll : CourseName(selected);
    }

    private void UpdateTopicLabel()
    {
        if (IsAllRhythms) return;
        _topicTab.Text = TopicLabel();
    }

    private void UpdateItemLabel()
    {
        if (IsAllRhythms)
        {
            var rhythm = _rhythmViewModel?.SelectedRhythm;
            _itemTab.Text = rhythm is null ? AppStrings.RhythmSearchPlaceholder : RhythmName(rhythm);
            return;
        }
        // A group Тема's Подтема, or a plain lecture for a flat course.
        var lecture = _courseViewer?.SelectedLecture;
        var placeholder = HasTopics ? AppStrings.SubtopicSelectorTitle : AppStrings.LectureSelectorTitle;
        _itemTab.Text = lecture is null ? placeholder : LectureName(lecture);
    }

    /// <summary>The label for the topic selector, derived from <see cref="_selectedTopicId"/>.</summary>
    private string TopicLabel()
    {
        if (SelectedTopic is { } topic) return CourseTopicFlyout.TopicName(topic, IsRussian);
        // A loose (topic-less) lecture picked from the Тема dropdown shows its own name here.
        var lecture = _courseViewer?.SelectedLecture;
        if (lecture is not null && _courseViewer?.SelectedCourse is { } course && CourseTopicFlyout.IsUngrouped(course, lecture))
            return LectureName(lecture);
        return AppStrings.TopicSelectorTitle;
    }

    private TopicEntry? SelectedTopic =>
        _selectedTopicId is null
            ? null
            : _courseViewer?.SelectedCourse?.Topics.FirstOrDefault(t => t.Id == _selectedTopicId);

    /// <summary>True when the selected Тема is a group (holds Подтемы) — the only case with a subtopic tab.</summary>
    private bool IsGroupSelected => SelectedTopic is { IsLeaf: false };

    /// <summary>Reflects the current Тема from the open lecture: its owning Тема (group or leaf), or
    /// null when no lecture is open or the lecture has no authored Тема.</summary>
    private void SyncTopicFromLecture()
    {
        var lecture = _courseViewer?.SelectedLecture;
        _selectedTopicId = lecture is not null && _courseViewer?.SelectedCourse is { } course
                           && course.Topics.Any(t => t.Id == lecture.Topic)
            ? lecture.Topic
            : null;
    }

    /// <summary>
    /// In "All rhythms" mode, make sure a rhythm is selected: prefer the last-used rhythm, else the
    /// first available. persist:false keeps the saved last-rhythm pref intact for auto-selections.
    /// </summary>
    private void EnsureRhythmSelected()
    {
        var vm = _rhythmViewModel;
        if (vm is null || vm.SelectedRhythm is not null || vm.Rhythms.Count == 0) return;
        var lastId = _appViewModel?.Prefs?.LastRhythmId;
        var target = lastId is not null && vm.Rhythms.Any(r => r.Id == lastId) ? lastId : vm.Rhythms[0].Id;
        vm.SelectRhythm(target, persist: false);
    }

    private bool IsRussian => _appViewModel?.SelectedLanguage == DomainLanguage.RU;

    private string CourseName(CourseEntry course) =>
        IsRussian ? course.NameRu ?? course.TitleEn : course.TitleEn;

    private string LectureName(LectureEntry lecture) =>
        IsRussian ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;

    private string RhythmName(PathologyEntry rhythm) =>
        IsRussian ? rhythm.NameRu ?? rhythm.TitleEn : rhythm.TitleEn;

    private void OnCourseClick(object? sender, EventArgs e)
    {
        if (_appViewModel is null) return;
        var flyout = new MenuFlyout();

        var all = new MenuFlyoutItem { Text = AppStrings.RhythmCourseFilterAll };
        all.Click += (_, _) => _appViewModel.SelectCourse(null);
        flyout.Items.Add(all);

        foreach (var course in _appViewModel.Courses)
        {
            var captured = course;
            var item = new MenuFlyoutItem { Text = CourseName(course) };
            item.Click += (_, _) => _appViewModel.SelectCourse(captured.Id);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_courseTab);
    }

    private void OnTopicClick(object? sender, EventArgs e)
    {
        if (IsAllRhythms || _appViewModel is null || _courseViewer?.SelectedCourse is not { } course) return;
        var flyout = CourseTopicFlyout.BuildTopics(course, _appViewModel.SelectedLanguage, SelectTopic, SelectLooseLecture);
        flyout.ShowAt(_topicTab);
    }

    /// <summary>Opens a topic-less lecture chosen directly from the Тема dropdown (no subtopic tab).</summary>
    private void SelectLooseLecture(string lectureId)
    {
        if (_appViewModel is null || _courseViewer is null) return;
        _selectedTopicId = null;
        _courseViewer.SelectLecture(lectureId, _appViewModel.SelectedLanguage.Tag());
        UpdateSelectors();
    }

    /// <summary>
    /// Applies a Тема chosen from the topic dropdown: a leaf Тема opens its own content directly (no
    /// subtopic tab); a group Тема auto-opens its first Подтема so a lecture shows and the subtopic tab
    /// appears populated.
    /// </summary>
    private void SelectTopic(string topicId)
    {
        if (_appViewModel is null || _courseViewer?.SelectedCourse is not { } course) return;
        _selectedTopicId = topicId;
        var langTag = _appViewModel.SelectedLanguage.Tag();

        if (course.Topics.FirstOrDefault(t => t.Id == topicId) is { IsLeaf: true })
        {
            _courseViewer.SelectLecture(topicId, langTag);
        }
        else if (CourseTopicFlyout.Subtopics(course, topicId).FirstOrDefault() is { } first)
        {
            _courseViewer.SelectLecture(first.Id, langTag);
        }
        // SelectLecture raises SelectedLecture → OnCourseViewerChanged refreshes everything; refresh
        // here too for an empty group (no lecture opened) so the tabs still update.
        UpdateSelectors();
    }

    private void OnItemClick(object? sender, EventArgs e)
    {
        if (IsAllRhythms) ShowRhythmFlyout();
        else if (HasTopics) ShowSubtopicFlyout();
        else ShowLectureFlyout();
    }

    private void ShowSubtopicFlyout()
    {
        if (_appViewModel is null || _courseViewer?.SelectedCourse is not { } course || !IsGroupSelected) return;
        var langTag = _appViewModel.SelectedLanguage.Tag();
        var flyout = CourseTopicFlyout.BuildSubtopics(course, _selectedTopicId, _appViewModel.SelectedLanguage,
            lectureId => _courseViewer.SelectLecture(lectureId, langTag));
        flyout.ShowAt(_itemTab);
    }

    /// <summary>The flat lecture picker for a course with no Темы.</summary>
    private void ShowLectureFlyout()
    {
        if (_appViewModel is null || _courseViewer?.SelectedCourse is not { } course) return;
        var langTag = _appViewModel.SelectedLanguage.Tag();
        var flyout = CourseTopicFlyout.BuildLectures(course, _appViewModel.SelectedLanguage,
            lectureId => _courseViewer.SelectLecture(lectureId, langTag));
        flyout.ShowAt(_itemTab);
    }

    private void ShowRhythmFlyout()
    {
        if (_rhythmViewModel is null || _appViewModel is null) return;
        // Same grouped-and-searchable panel as the Teaching left drawer, in a dropdown off the tab.
        var panel = new RhythmChoosingPanel
        {
            DisplayLanguage = _appViewModel.SelectedLanguage,
            ShowPinButton = false, // pinning is meaningless in a dropdown
            Width = 300,
            Height = 440,
        };
        panel.SetRhythms(_rhythmViewModel.Rhythms);
        panel.SelectedId = _rhythmViewModel.SelectedRhythm?.Id;
        var flyout = new Flyout { Content = panel };
        // Commit on an explicit tap only (not on auto-select while filtering).
        panel.RhythmInvoked += (_, entry) =>
        {
            _rhythmViewModel.SelectRhythm(entry.Id);
            flyout.Hide();
        };
        flyout.ShowAt(_itemTab);
    }
}
