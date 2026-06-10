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
/// pathologies, "All rhythms" clears it) plus a start/stop button. Port of the Android
/// <c>TeachingControlPanel</c>.
/// </summary>
public sealed class TeachingControlPanel : UserControl
{
    private static readonly string GlyphPlay = char.ConvertFromUtf32(0xE768);
    private static readonly string GlyphStop = char.ConvertFromUtf32(0xE71A);

    private readonly Tab _courseTab = new();
    private readonly Tab _startStopTab = new();
    private MonitorViewModel? _monitorViewModel;
    private AppViewModel? _appViewModel;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

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

        _startStopTab.Glyph = GlyphPlay;
        _startStopTab.Margin = new Thickness(4, 0, 4, 0);
        _startStopTab.Click += OnStartStopClick;
        row.Children.Add(_startStopTab);

        Content = row;
    }

    public void Bind(MonitorViewModel monitorViewModel, AppViewModel appViewModel)
    {
        if (_monitorViewModel is not null) _monitorViewModel.PropertyChanged -= OnVmChanged;
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnAppChanged;
        _monitorViewModel = monitorViewModel;
        _appViewModel = appViewModel;
        _monitorViewModel.PropertyChanged += OnVmChanged;
        _appViewModel.PropertyChanged += OnAppChanged;
        UpdateGlyph();
        UpdateCourseLabel();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode)) UpdateGlyph();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.SelectedCourseId)
            or nameof(AppViewModel.Courses)
            or nameof(AppViewModel.SelectedLanguage))
        {
            UpdateCourseLabel();
        }
    }

    private void UpdateGlyph()
    {
        if (_monitorViewModel is null) return;
        _startStopTab.Glyph = _monitorViewModel.MonitorMode.IsRunning ? GlyphStop : GlyphPlay;
    }

    private void UpdateCourseLabel()
    {
        if (_appViewModel is null) return;
        var selected = _appViewModel.SelectedCourseId is { } id
            ? _appViewModel.Courses.FirstOrDefault(c => c.Id == id)
            : null;
        _courseTab.Text = selected is null ? AppStrings.RhythmCourseFilterAll : CourseName(selected);
    }

    private string CourseName(CourseEntry course) =>
        _appViewModel?.SelectedLanguage == DomainLanguage.RU ? course.NameRu ?? course.TitleEn : course.TitleEn;

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

    private void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_monitorViewModel is null) return;
        var newState = !_monitorViewModel.MonitorMode.IsRunning;
        _monitorViewModel.SetIsRunning(newState);
        StartStopClick?.Invoke(this, newState);
    }
}
