using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Teaching-mode top sub-panel: a course selector plus a context-sensitive second selector.
/// When a course is selected the second selector picks a lecture (shown in the main course viewer);
/// when "All rhythms" is selected it switches to a rhythm picker (the monitor's rhythm), defaulting
/// to the last-used rhythm, or the first if none. Port of the Android <c>TeachingControlPanel</c>.
/// </summary>
public sealed class TeachingControlPanel : UserControl
{
    private readonly Tab _courseTab = new();
    private readonly Tab _itemTab = new();
    private AppViewModel? _appViewModel;
    private CourseViewerViewModel? _courseViewer;
    private RhythmViewModel? _rhythmViewModel;

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
        _courseTab.VerticalAlignment = VerticalAlignment.Center;
        _courseTab.Click += OnCourseClick;
        row.Children.Add(_courseTab);

        _itemTab.Margin = new Thickness(4, 0, 4, 0);
        _itemTab.MinWidth = 120;
        _itemTab.ShowChevron = true;
        _itemTab.VerticalAlignment = VerticalAlignment.Center;
        _itemTab.Click += OnItemClick;
        row.Children.Add(_itemTab);

        Content = row;
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
        UpdateCourseLabel();
        UpdateItemLabel();
        if (IsAllRhythms) EnsureRhythmSelected();
    }

    /// <summary>True when no course is selected ("All rhythms"); the item selector then picks a
    /// rhythm instead of a lecture.</summary>
    private bool IsAllRhythms => _appViewModel is not null && _appViewModel.SelectedCourseId is null;

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedCourseId)
            or nameof(AppViewModel.Courses)
            or nameof(AppViewModel.SelectedLanguage))
        {
            UpdateCourseLabel();
            UpdateItemLabel();
            if (e.PropertyName == nameof(AppViewModel.SelectedCourseId) && IsAllRhythms)
                EnsureRhythmSelected();
        }
    }

    private void OnCourseViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The course viewer drives lecture state; the selector here just reflects it.
        if (e.PropertyName is nameof(CourseViewerViewModel.SelectedLecture)
            or nameof(CourseViewerViewModel.SelectedCourse))
        {
            UpdateItemLabel();
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

    private void UpdateCourseLabel()
    {
        if (_appViewModel is null) return;
        var selected = _appViewModel.SelectedCourseId is { } id
            ? _appViewModel.Courses.FirstOrDefault(c => c.Id == id)
            : null;
        _courseTab.Text = selected is null ? AppStrings.RhythmCourseFilterAll : CourseName(selected);
    }

    private void UpdateItemLabel()
    {
        if (IsAllRhythms)
        {
            var rhythm = _rhythmViewModel?.SelectedRhythm;
            _itemTab.Text = rhythm is null ? AppStrings.RhythmSearchPlaceholder : RhythmName(rhythm);
        }
        else
        {
            var lecture = _courseViewer?.SelectedLecture;
            _itemTab.Text = lecture is null ? AppStrings.LectureSelectorTitle : LectureName(lecture);
        }
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

    private string CourseName(CourseEntry course) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? course.NameRu ?? course.TitleEn : course.TitleEn;

    private string LectureName(LectureEntry lecture) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? lecture.NameRu ?? lecture.TitleEn : lecture.TitleEn;

    private string RhythmName(PathologyEntry rhythm) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? rhythm.NameRu ?? rhythm.TitleEn : rhythm.TitleEn;

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

    private void OnItemClick(object? sender, EventArgs e)
    {
        if (IsAllRhythms) ShowRhythmFlyout();
        else ShowLectureFlyout();
    }

    private void ShowLectureFlyout()
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
        flyout.ShowAt(_itemTab);
    }

    private void ShowRhythmFlyout()
    {
        if (_rhythmViewModel is null) return;
        var flyout = new MenuFlyout();
        foreach (var rhythm in _rhythmViewModel.Rhythms)
        {
            var captured = rhythm;
            var item = new MenuFlyoutItem { Text = RhythmName(rhythm) };
            item.Click += (_, _) => _rhythmViewModel.SelectRhythm(captured.Id);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_itemTab);
    }
}
