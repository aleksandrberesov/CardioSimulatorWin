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
/// Course-Constructor top sub-panel: a course selector plus a lecture selector, mirroring the
/// Teaching mode's <see cref="TeachingControlPanel"/> so the constructor is driven from the app
/// top bar instead of an in-body list. On first appearance it auto-selects the first course and
/// its first lecture (restoring the last-used course/lecture when available) and persists the
/// user's choice for next time. Selection is applied to the shared
/// <see cref="CourseConstructorViewModel"/>, which the <c>CourseConstructorScreen</c> body reflects.
/// </summary>
public sealed class CourseConstructorControlPanel : UserControl
{
    private readonly Tab _courseTab = new();
    private readonly Tab _lectureTab = new();
    private AppViewModel? _appViewModel;
    private CourseConstructorViewModel? _vm;

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
        UpdateCourseLabel();
        UpdateLectureLabel();
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
        {
            UpdateCourseLabel();
            UpdateLectureLabel();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CourseConstructorViewModel.SelectedCourse):
                UpdateCourseLabel();
                UpdateLectureLabel();
                if (_appViewModel is not null)
                    _appViewModel.Prefs.CourseCtorCourseId = _vm?.SelectedCourse?.Id;
                break;
            case nameof(CourseConstructorViewModel.SelectedLecture):
                UpdateLectureLabel();
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
            UpdateCourseLabel();
            UpdateLectureLabel();
        });
    }

    /// <summary>
    /// First-appearance defaults: select the first course and its first lecture, preferring the
    /// last-used course/lecture when they still exist. No-op once something is already selected
    /// (the view-model is a session singleton, so a mid-session re-entry keeps the user's place).
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

        if (_vm.SelectedLecture is null && _vm.SelectedCourse is { Lectures.Count: > 0 } course)
        {
            var lectureId = savedLecture is not null && course.Lectures.Any(l => l.Id == savedLecture)
                ? savedLecture
                : course.Lectures[0].Id;
            _vm.SelectLecture(lectureId, _appViewModel.SelectedLanguage.Tag());
        }
    }

    /// <summary>Selects the first lecture of the current course (used after an explicit course switch).</summary>
    private void SelectFirstLecture()
    {
        if (_vm?.SelectedCourse is { Lectures.Count: > 0 } course && _appViewModel is not null)
            _vm.SelectLecture(course.Lectures[0].Id, _appViewModel.SelectedLanguage.Tag());
    }

    private void UpdateCourseLabel()
    {
        var course = _vm?.SelectedCourse;
        _courseTab.Text = course is null ? AppStrings.CourseSelectorTitle : CourseName(course);
    }

    private void UpdateLectureLabel()
    {
        var lecture = _vm?.SelectedLecture;
        _lectureTab.Text = lecture is null ? AppStrings.SubtopicSelectorTitle : LectureName(lecture);
    }

    private bool IsRussian => _appViewModel?.SelectedLanguage == DomainLanguage.RU;

    private string CourseName(Course course) => IsRussian ? course.NameRu ?? course.TitleEn : course.TitleEn;
    private string CourseName(CourseEntry course) => IsRussian ? course.NameRu ?? course.TitleEn : course.TitleEn;
    private string LectureName(LectureEntry lecture) => IsRussian ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;

    private void OnCourseClick(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var flyout = new MenuFlyout();
        foreach (var course in _vm.Repository.Courses)
        {
            var captured = course;
            var item = new MenuFlyoutItem { Text = CourseName(course) };
            item.Click += (_, _) =>
            {
                if (captured.Id == _vm.SelectedCourse?.Id) return;
                _vm.SelectCourse(captured.Id);
                SelectFirstLecture();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_courseTab);
    }

    private void OnLectureClick(object? sender, EventArgs e)
    {
        if (_vm is null || _appViewModel is null || _vm.SelectedCourse is not { } course) return;
        var langTag = _appViewModel.SelectedLanguage.Tag();
        // Nested Тема → Подтема menu: topics expand to their subtopics, which open on click.
        var flyout = CourseTopicFlyout.Build(course, _appViewModel.SelectedLanguage,
            lectureId => _vm.SelectLecture(lectureId, langTag));
        flyout.ShowAt(_lectureTab);
    }
}
