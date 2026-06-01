using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Teaching mode: the monitor with a left rhythm drawer + a right course drawer. Picking a
/// lecture switches the middle pane from the monitor to the lecture content (Phase 9 placeholder
/// — Markdown + KaTeX rendering lands with Phase 10). Picking a rhythm restores the monitor.
/// Port of the Android <c>TeachingScreen</c> (course-aware variant, Phase 2 of the Course
/// Constructor plan).
/// </summary>
public sealed class TeachingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly RhythmChoosingDrawer _rhythmDrawer = new();
    private readonly CourseSelectorDrawer _courseDrawer = new();
    private readonly Border _lecturePane;
    private readonly Grid _contentHost;
    private RhythmViewModel? _rhythmVm;
    private CourseViewerViewModel? _courseVm;
    private AppViewModel? _appVm;

    public TeachingScreen()
    {
        // Lecture content is rendered into this Border each time a lecture is selected.
        _lecturePane = new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xFA, G = 0xFA, B = 0xFA }),
            Visibility = Visibility.Collapsed,
        };

        _contentHost = new Grid();
        _monitor.Margin = new Thickness(24, 0, 0, 0);
        _contentHost.Children.Add(_monitor);
        _contentHost.Children.Add(_lecturePane);

        var grid = new Grid();
        grid.Children.Add(_contentHost);

        _rhythmDrawer.HorizontalAlignment = HorizontalAlignment.Left;
        _rhythmDrawer.VerticalAlignment = VerticalAlignment.Stretch;
        grid.Children.Add(_rhythmDrawer);

        _courseDrawer.HorizontalAlignment = HorizontalAlignment.Right;
        _courseDrawer.VerticalAlignment = VerticalAlignment.Stretch;
        grid.Children.Add(_courseDrawer);

        Content = grid;
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _courseVm = appVm.CourseViewerViewModel;

        _monitor.Bind(monitorVm, rhythmVm);
        _rhythmDrawer.DisplayLanguage = appVm.SelectedLanguage;
        _rhythmDrawer.SetRhythms(rhythmVm.Rhythms);
        _rhythmDrawer.SelectedId = rhythmVm.SelectedRhythm?.Id;
        _rhythmDrawer.RhythmSelected += (_, entry) =>
        {
            rhythmVm.SelectRhythm(entry.Id);
            ShowMonitor();
        };

        _courseDrawer.Bind(appVm, _courseVm);
        _courseDrawer.LectureSelected += (_, _, _) => ShowLecture();

        _courseVm.PropertyChanged += OnCourseChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        appVm.PropertyChanged += OnAppChanged;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _rhythmDrawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
            ShowLecture(); // re-render lecture title in new language if active
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
        {
            _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
        }
        else if (e.PropertyName == nameof(RhythmViewModel.SelectedRhythm))
        {
            _rhythmDrawer.SelectedId = _rhythmVm.SelectedRhythm?.Id;
        }
    }

    private void OnCourseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CourseViewerViewModel.LectureContent)) ShowLecture();
    }

    /// <summary>Switches the central pane to the lecture content rendered via <see cref="CourseLectureRenderer"/>.</summary>
    private void ShowLecture()
    {
        if (_courseVm?.LectureContent is null || _appVm is null)
        {
            ShowMonitor();
            return;
        }
        _lecturePane.Child = CourseLectureRenderer.Render(_courseVm.LectureContent, _appVm);
        _lecturePane.Visibility = Visibility.Visible;
        _monitor.Visibility = Visibility.Collapsed;
    }

    private void ShowMonitor()
    {
        _lecturePane.Visibility = Visibility.Collapsed;
        _monitor.Visibility = Visibility.Visible;
    }
}
