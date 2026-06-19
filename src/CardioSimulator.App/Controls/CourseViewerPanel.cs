using System.ComponentModel;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The Teaching screen's main panel: a persistent course viewer hosting a <see cref="LectureWebView"/>.
/// A monitor button in the top bar pops the <see cref="MonitorViewerOverlay"/> over it. Both the
/// course and lecture selectors live in the top panel's <see cref="TeachingControlPanel"/>. This is
/// the Windows inversion of the Android layout (where the monitor is the base and the course viewer
/// is the overlay).
/// </summary>
public sealed class CourseViewerPanel : UserControl
{
    private static readonly string GlyphMonitor = char.ConvertFromUtf32(0xE95E); // "Health" heart/pulse glyph

    private readonly LectureWebView _web = new();
    private readonly TextBlock _placeholder = new()
    {
        Text = "Select a lecture",
        Foreground = new SolidColorBrush(Colors.Gray),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private AppViewModel? _appVm;
    private CourseViewerViewModel? _viewer;
    private string? _selectedCourseId;

    /// <summary>Raised when the top-bar monitor button is tapped.</summary>
    public event EventHandler? OpenMonitorRequested;

    public CourseViewerPanel()
    {
        Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xFA, G = 0xFA, B = 0xFA });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: just the monitor button (right-aligned). It lives here rather than floated over
        // the content because the LectureWebView is a native airspace surface that renders above
        // XAML siblings; a button floated over the web region would be hidden behind it.
        var topBar = new Grid { Height = 56, Padding = new Thickness(16, 0, 8, 0), Background = new SolidColorBrush(Colors.WhiteSmoke) };
        var monitorButton = new Button
        {
            Content = new FontIcon { Glyph = GlyphMonitor },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        monitorButton.Click += (_, _) => OpenMonitorRequested?.Invoke(this, EventArgs.Empty);
        topBar.Children.Add(monitorButton);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        // Content: web view (or placeholder). The lecture selector now lives in the top panel.
        var content = new Grid();
        content.Children.Add(_web);
        content.Children.Add(_placeholder);
        _web.Visibility = Visibility.Collapsed;

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        Content = root;
    }

    public void Bind(AppViewModel appVm, CourseViewerViewModel viewer)
    {
        _appVm = appVm;
        _viewer = viewer;
        viewer.PropertyChanged += OnViewerChanged;
        appVm.PropertyChanged += OnAppChanged;
        SyncSelectedCourse();
    }

    private void SyncSelectedCourse()
    {
        if (_appVm is null) return;
        var newId = _appVm.SelectedCourseId;
        if (_selectedCourseId == newId) return;
        _selectedCourseId = newId;
        if (_selectedCourseId is null) return;
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
        if (_viewer.SelectedCourse?.Lectures is { Count: > 0 } lectures)
            _viewer.SelectLecture(lectures[0].Id, _appVm.SelectedLanguage.Tag());
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedCourseId))
        {
            SyncSelectedCourse();
        }
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CourseViewerViewModel.LectureContent)) ShowLecture();
    }

    private void ShowLecture()
    {
        if (_viewer?.LectureContent is null || _appVm is null)
        {
            _web.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
        }
        else
        {
            _web.SetLecture(_viewer.LectureContent, EcgTraceResolver.ForRepository(_appVm.Repository));
            _web.Visibility = Visibility.Visible;
            _placeholder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-syncs the current course selection from the top panel (called when re-shown).</summary>
    public void Refresh()
    {
        SyncSelectedCourse();
    }
}
