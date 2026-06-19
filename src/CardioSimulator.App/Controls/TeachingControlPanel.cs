using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Teaching-mode top sub-panel: a course selector (filters the rhythm list to the course's
/// pathologies, "All rhythms" clears it) and a lecture selector (picks the lecture shown in the
/// main course viewer). Port of the Android <c>TeachingControlPanel</c>.
/// </summary>
public sealed class TeachingControlPanel : UserControl
{
    private readonly Tab _courseTab = new();
    private readonly Tab _lectureTab = new();
    private AppViewModel? _appViewModel;
    private CourseViewerViewModel? _courseViewer;

    public TeachingControlPanel()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _courseTab.Margin = new Thickness(4, 0, 4, 0);
        _courseTab.MinWidth = 120;
        _courseTab.Click += OnCourseClick;
        row.Children.Add(_courseTab);

        _lectureTab.Margin = new Thickness(4, 0, 4, 0);
        _lectureTab.MinWidth = 120;
        _lectureTab.Click += OnLectureClick;
        row.Children.Add(_lectureTab);

        Content = row;
    }

    public void Bind(AppViewModel appViewModel)
    {
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnAppChanged;
        if (_courseViewer is not null) _courseViewer.PropertyChanged -= OnCourseViewerChanged;
        _appViewModel = appViewModel;
        _courseViewer = appViewModel.CourseViewerViewModel;
        _appViewModel.PropertyChanged += OnAppChanged;
        _courseViewer.PropertyChanged += OnCourseViewerChanged;
        UpdateCourseLabel();
        UpdateLectureLabel();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedCourseId)
            or nameof(AppViewModel.Courses)
            or nameof(AppViewModel.SelectedLanguage))
        {
            UpdateCourseLabel();
            UpdateLectureLabel();
        }
    }

    private void OnCourseViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The course viewer drives lecture state; the selector here just reflects it.
        if (e.PropertyName is nameof(CourseViewerViewModel.SelectedLecture)
            or nameof(CourseViewerViewModel.SelectedCourse))
        {
            UpdateLectureLabel();
        }
    }

    private void UpdateCourseLabel()
    {
        if (_appViewModel is null) return;
        var selected = _appViewModel.SelectedCourseId is { } id
            ? _appViewModel.Courses.FirstOrDefault(c => c.Id == id)
            : null;
        _courseTab.Text = selected is null ? AppStrings.RhythmCourseFilterAll : CourseName(selected);
    }

    private void UpdateLectureLabel()
    {
        var lecture = _courseViewer?.SelectedLecture;
        _lectureTab.Text = lecture is null ? AppStrings.LectureSelectorTitle : LectureName(lecture);
    }

    private string CourseName(CourseEntry course) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? course.NameRu ?? course.TitleEn : course.TitleEn;

    private string LectureName(LectureEntry lecture) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;

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

    private void OnLectureClick(object? sender, EventArgs e)
    {
        if (_appViewModel is null || _courseViewer?.SelectedCourse is not { } course) return;
        var langTag = _appViewModel.SelectedLanguage.Tag();
        var flyout = new MenuFlyout();
        foreach (var lecture in course.Lectures)
        {
            var captured = lecture;
            var item = new MenuFlyoutItem { Text = LectureName(lecture) };
            item.Click += (_, _) => _courseViewer.SelectLecture(captured.Id, langTag);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_lectureTab);
    }
}
