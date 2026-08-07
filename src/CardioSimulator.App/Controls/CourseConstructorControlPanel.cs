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
/// Course-Constructor top sub-panel: a course selector plus adaptive selectors. A course with no Темы
/// (flat <c>Course → Lecture</c>) shows a plain lecture selector; a course with Темы shows a flat Тема
/// (topic) selector and — only when a <b>group</b> Тема is selected — a Подтема (subtopic) selector
/// (a <b>leaf</b> Тема is itself a lecture, so it shows no subtopic selector). Mirrors the Teaching mode's
/// <see cref="TeachingControlPanel"/> so the constructor is driven from the app top bar instead of an
/// in-body list. On first appearance it auto-selects the first course and its first content item
/// (restoring the last-used course/lecture when available) and persists the user's choice for next
/// time. Selection is applied to the shared <see cref="CourseConstructorViewModel"/>, which the
/// <c>CourseConstructorScreen</c> body reflects.
/// </summary>
public sealed class CourseConstructorControlPanel : UserControl
{
    private readonly Tab _courseTab = new();
    private readonly Tab _topicTab = new();
    private readonly Tab _lectureTab = new();
    private AppViewModel? _appViewModel;
    private CourseConstructorViewModel? _vm;

    /// <summary>The Тема currently selected (a leaf or group topic id), or null when none is chosen.</summary>
    private string? _selectedTopicId;

    public CourseConstructorControlPanel()
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

        _lectureTab.Margin = new Thickness(4, 0, 4, 0);
        _lectureTab.MinWidth = 120;
        _lectureTab.ShowChevron = true;
        _lectureTab.Large = true;
        _lectureTab.VerticalAlignment = VerticalAlignment.Center;
        _lectureTab.Click += OnLectureClick;
        row.Children.Add(_lectureTab);

        Content = row;
        Unloaded += (_, _) => Detach();
    }

    public void Bind(AppViewModel appViewModel)
    {
        Detach();
        _appViewModel = appViewModel;
        _vm = appViewModel.CourseConstructorViewModel;
        _appViewModel.PropertyChanged += OnAppChanged;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Repository.ManifestChanged += OnManifestChanged;

        EnsureSelection();
        SyncTopicFromLecture();
        UpdateSelectors();
    }

    private void Detach()
    {
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnAppChanged;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Repository.ManifestChanged -= OnManifestChanged;
        }
        _appViewModel = null;
        _vm = null;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage))
            UpdateSelectors();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CourseConstructorViewModel.SelectedCourse):
                SyncTopicFromLecture();
                UpdateSelectors();
                if (_appViewModel is not null)
                    _appViewModel.Prefs.CourseCtorCourseId = _vm?.SelectedCourse?.Id;
                break;
            case nameof(CourseConstructorViewModel.SelectedLecture):
                SyncTopicFromLecture();
                UpdateSelectors();
                if (_appViewModel is not null)
                    _appViewModel.Prefs.CourseCtorLectureId = _vm?.SelectedLecture?.Id;
                break;
        }
    }

    private void OnManifestChanged(object? sender, EventArgs e)
    {
        // The course list loads asynchronously; retry the initial auto-selection once it arrives,
        // and refresh the labels in case a course/lecture title changed.
        DispatcherQueue.TryEnqueue(() =>
        {
            EnsureSelection();
            SyncTopicFromLecture();
            UpdateSelectors();
        });
    }

    /// <summary>
    /// First-appearance defaults: select the first course and its first content item, preferring the
    /// last-used course/lecture when they still exist. No-op once something is already selected
    /// (the view-model is a session singleton, so a mid-session re-entry keeps the user's place).
    /// A pack reload re-opens the current course/lecture itself (see
    /// <c>AppViewModel.ReopenAfterCourseReload</c>), so a stale selection never reaches here.
    /// </summary>
    private void EnsureSelection()
    {
        if (_vm is null || _appViewModel is null) return;
        var courses = _vm.Repository.Courses;
        if (courses.Count == 0) return;

        // Capture saved ids up front — selecting a course clears the lecture and rewrites the pref.
        var savedCourse = _appViewModel.Prefs.CourseCtorCourseId;
        var savedLecture = _appViewModel.Prefs.CourseCtorLectureId;

        if (_vm.SelectedCourse is null)
        {
            var courseId = savedCourse is not null && courses.Any(c => c.Id == savedCourse)
                ? savedCourse
                : courses[0].Id;
            _vm.SelectCourse(courseId);
        }

        if (_vm.SelectedLecture is null && _vm.SelectedCourse is { } course)
        {
            // Restore the last-opened content item (Подтема or leaf Тема) when it still exists,
            // else fall back to the course's first item.
            var lectureId = savedLecture is not null && course.ContentItem(savedLecture) is not null
                ? savedLecture
                : course.FirstContentItemId();
            if (lectureId is not null)
                _vm.SelectLecture(lectureId, _appViewModel.SelectedLanguage.Tag());
        }
    }

    /// <summary>Selects the first content item of the current course (used after an explicit course switch).</summary>
    private void SelectFirstLecture()
    {
        if (_vm?.SelectedCourse?.FirstContentItemId() is { } firstId && _appViewModel is not null)
            _vm.SelectLecture(firstId, _appViewModel.SelectedLanguage.Tag());
    }

    private void UpdateSelectors()
    {
        // Flat course (no Темы): course + lecture picker. Topic course: course + Тема, plus a Подтема
        // picker for a group Тема (none for a leaf Тема).
        _topicTab.Visibility = HasTopics ? Visibility.Visible : Visibility.Collapsed;
        _lectureTab.Visibility = !HasTopics || IsGroupSelected ? Visibility.Visible : Visibility.Collapsed;
        UpdateCourseLabel();
        UpdateTopicLabel();
        UpdateLectureLabel();
    }

    /// <summary>True when the selected course organizes its lectures into Темы (vs a flat lecture list).</summary>
    private bool HasTopics => _vm?.SelectedCourse is { } course && course.Topics.Count > 0;

    private void UpdateCourseLabel()
    {
        var course = _vm?.SelectedCourse;
        _courseTab.Text = course is null ? AppStrings.CourseSelectorTitle : CourseName(course);
    }

    private void UpdateTopicLabel() => _topicTab.Text = TopicLabel();

    private string TopicLabel()
    {
        if (SelectedTopic is { } topic) return CourseTopicFlyout.TopicName(topic, IsRussian);
        // A loose (topic-less) lecture picked from the Тема dropdown shows its own name here.
        var lecture = _vm?.SelectedLecture;
        if (lecture is not null && _vm?.SelectedCourse is { } course && CourseTopicFlyout.IsUngrouped(course, lecture))
            return LectureName(lecture);
        return AppStrings.TopicSelectorTitle;
    }

    private void UpdateLectureLabel()
    {
        // A group Тема's Подтема, or a plain lecture for a flat course.
        var lecture = _vm?.SelectedLecture;
        var placeholder = HasTopics ? AppStrings.SubtopicSelectorTitle : AppStrings.LectureSelectorTitle;
        _lectureTab.Text = lecture is null ? placeholder : LectureName(lecture);
    }

    private TopicEntry? SelectedTopic =>
        _selectedTopicId is null
            ? null
            : _vm?.SelectedCourse?.Topics.FirstOrDefault(t => t.Id == _selectedTopicId);

    /// <summary>True when the selected Тема is a group (holds Подтемы) — the only case with a subtopic tab.</summary>
    private bool IsGroupSelected => SelectedTopic is { IsLeaf: false };

    /// <summary>Reflects the current Тема from the open lecture: its owning Тема (group or leaf), or
    /// null when no lecture is open or the lecture has no authored Тема.</summary>
    private void SyncTopicFromLecture()
    {
        var lecture = _vm?.SelectedLecture;
        _selectedTopicId = lecture is not null && _vm?.SelectedCourse is { } course
                           && course.Topics.Any(t => t.Id == lecture.Topic)
            ? lecture.Topic
            : null;
    }

    private bool IsRussian => _appViewModel?.SelectedLanguage == DomainLanguage.RU;

    private string CourseName(Course course) => IsRussian ? course.NameRu ?? course.TitleEn : course.TitleEn;
    private string CourseName(CourseEntry course) => IsRussian ? course.NameRu ?? course.TitleEn : course.TitleEn;
    private string LectureName(LectureEntry lecture) => IsRussian ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;

    /// <summary>Prompts to save/discard when the open lecture has unsaved edits before switching away;
    /// returns true when the caller may proceed.</summary>
    private async Task<bool> ConfirmSwitchAsync() =>
        _vm is null || await UnsavedChangesDialog.ConfirmAsync(XamlRoot, _vm);

    /// <summary>Opens a lecture, guarded by the unsaved-changes prompt (the Подтема/lecture dropdown).</summary>
    private async void SelectLectureGuarded(string lectureId)
    {
        if (_vm is null || _appViewModel is null) return;
        if (!await ConfirmSwitchAsync()) return;
        _vm.SelectLecture(lectureId, _appViewModel.SelectedLanguage.Tag());
        UpdateSelectors();
    }

    private void OnCourseClick(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var flyout = new MenuFlyout();
        foreach (var course in _vm.Repository.Courses)
        {
            var captured = course;
            var item = new MenuFlyoutItem { Text = CourseName(course) };
            item.Click += async (_, _) =>
            {
                if (captured.Id == _vm.SelectedCourse?.Id) return;
                if (!await ConfirmSwitchAsync()) return;
                _vm.SelectCourse(captured.Id);
                SelectFirstLecture();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_courseTab);
    }

    private void OnTopicClick(object? sender, EventArgs e)
    {
        if (_vm is null || _appViewModel is null || _vm.SelectedCourse is not { } course) return;
        var flyout = CourseTopicFlyout.BuildTopics(course, _appViewModel.SelectedLanguage, SelectTopic, SelectLooseLecture);
        flyout.ShowAt(_topicTab);
    }

    /// <summary>Opens a topic-less lecture chosen directly from the Тема dropdown (no subtopic tab).
    /// Clears the focused Тема so New/Delete Тема don't act on a stale topic.</summary>
    private async void SelectLooseLecture(string lectureId)
    {
        if (_vm is null || _appViewModel is null) return;
        if (!await ConfirmSwitchAsync()) return;
        _selectedTopicId = null;
        _vm.SelectedTopicId = null;
        _vm.SelectLecture(lectureId, _appViewModel.SelectedLanguage.Tag());
        UpdateSelectors();
    }

    /// <summary>
    /// Applies a Тема chosen from the topic dropdown. A leaf Тема opens its own content directly (no
    /// subtopic tab); a group Тема auto-opens its first Подтема (or, when empty, just focuses the
    /// group so a Подтема can be added under it). The focused Тема is pushed to the view-model so the
    /// toolbar's New/Delete Тема act on it.
    /// </summary>
    private async void SelectTopic(string topicId)
    {
        if (_vm is null || _appViewModel is null || _vm.SelectedCourse is null) return;
        if (!await ConfirmSwitchAsync()) return;
        if (_vm.SelectedCourse is not { } course) return; // DiscardChanges may have reloaded the course
        _selectedTopicId = topicId;
        _vm.SelectedTopicId = topicId;
        var langTag = _appViewModel.SelectedLanguage.Tag();

        if (course.Topics.FirstOrDefault(t => t.Id == topicId) is { IsLeaf: true })
        {
            _vm.SelectLecture(topicId, langTag);
        }
        else if (CourseTopicFlyout.Subtopics(course, topicId).FirstOrDefault() is { } first)
        {
            _vm.SelectLecture(first.Id, langTag);
        }
        // SelectLecture raises SelectedLecture → OnVmChanged refreshes everything; refresh here too for
        // an empty group (no lecture opened) so the tabs still update.
        UpdateSelectors();
    }

    private void OnLectureClick(object? sender, EventArgs e)
    {
        if (_vm is null || _appViewModel is null || _vm.SelectedCourse is not { } course) return;
        // A group Тема's Подтемы, or the flat lecture list when the course has no Темы. Selection is
        // guarded by the unsaved-changes prompt (SelectLectureGuarded).
        var flyout = HasTopics
            ? (IsGroupSelected
                ? CourseTopicFlyout.BuildSubtopics(course, _selectedTopicId, _appViewModel.SelectedLanguage, SelectLectureGuarded)
                : null)
            : CourseTopicFlyout.BuildLectures(course, _appViewModel.SelectedLanguage, SelectLectureGuarded);
        flyout?.ShowAt(_lectureTab);
    }
}
