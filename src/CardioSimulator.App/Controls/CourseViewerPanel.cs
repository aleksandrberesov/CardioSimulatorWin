using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The Teaching screen's main panel. With a course selected it hosts the lecture
/// <see cref="LectureWebView"/>; in "All rhythms" mode it shows a description card for the selected
/// pathology (composed from manifest data: name, lead count, ECG markers). A monitor button in the
/// top bar pops the <see cref="MonitorViewerOverlay"/> over it. Both selectors live in the top
/// panel's <see cref="TeachingControlPanel"/>. This is the Windows inversion of the Android layout
/// (where the monitor is the base and the course viewer is the overlay).
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
    private readonly StackPanel _rhythmCard = new()
    {
        Spacing = 6,
        Padding = new Thickness(40),
        MaxWidth = 640,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private AppViewModel? _appVm;
    private CourseViewerViewModel? _viewer;
    private RhythmViewModel? _rhythmVm;
    private string? _selectedCourseId;

    /// <summary>Raised when the monitor should open. The payload is null for the top-bar button, or
    /// carries the embed's pathology/leads/scheme when triggered from an <c>&lt;ecg&gt;</c> button.</summary>
    public event EventHandler<EcgMonitorRequest?>? OpenMonitorRequested;

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
        monitorButton.Click += (_, _) => OpenMonitorRequested?.Invoke(this, null);
        topBar.Children.Add(monitorButton);

        // An <ecg> embed's inline "open on monitor" button → open the monitor with that rhythm.
        _web.EcgOpenMonitorRequested += req => OpenMonitorRequested?.Invoke(this, req);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        // Content: the lecture web view, the rhythm description card, or the placeholder — exactly
        // one is visible at a time (see UpdateContentArea). The lecture selector lives in the top panel.
        var content = new Grid();
        content.Children.Add(_web);
        content.Children.Add(_rhythmCard);
        content.Children.Add(_placeholder);
        _web.Visibility = Visibility.Collapsed;

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        Content = root;
    }

    public void Bind(AppViewModel appVm, CourseViewerViewModel viewer, RhythmViewModel rhythmVm)
    {
        _appVm = appVm;
        _viewer = viewer;
        _rhythmVm = rhythmVm;
        viewer.PropertyChanged += OnViewerChanged;
        appVm.PropertyChanged += OnAppChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        SyncSelectedCourse();
        UpdateContentArea();
    }

    /// <summary>True when no course is selected ("All rhythms"); the panel then shows the selected
    /// pathology's description instead of a lecture.</summary>
    private bool IsAllRhythms => _appVm is not null && _appVm.SelectedCourseId is null;

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
        if (_viewer.SelectedCourse?.Lectures is { Count: > 0 } lectures)
            _viewer.SelectLecture(lectures[0].Id, _appVm.SelectedLanguage.Tag());
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage))
        {
            UpdateContentArea();
        }
        else if (e.PropertyName == nameof(AppViewModel.SelectedCourseId))
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

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SelectedRhythm carries the name/lead count; SignificantPoints carries the ECG markers.
        if (e.PropertyName is nameof(RhythmViewModel.SelectedRhythm)
            or nameof(RhythmViewModel.SignificantPoints))
        {
            UpdateContentArea();
        }
    }

    /// <summary>Shows exactly one of: the pathology card ("All rhythms" + a rhythm), the lecture web
    /// view (a lecture is loaded), or the placeholder.</summary>
    private void UpdateContentArea()
    {
        if (IsAllRhythms && _rhythmVm?.SelectedRhythm is { } rhythm)
        {
            BuildRhythmCard(rhythm);
            _rhythmCard.Visibility = Visibility.Visible;
            _web.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        else if (_viewer?.LectureContent is not null)
        {
            _web.Visibility = Visibility.Visible;
            _rhythmCard.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            _placeholder.Visibility = Visibility.Visible;
            _web.Visibility = Visibility.Collapsed;
            _rhythmCard.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildRhythmCard(PathologyEntry entry)
    {
        _rhythmCard.Children.Clear();
        var ru = _appVm?.SelectedLanguage == DomainLanguage.RU;
        var primary = ru ? entry.NameRu ?? entry.TitleEn : entry.TitleEn;
        var secondary = ru ? entry.TitleEn : entry.NameRu;

        _rhythmCard.Children.Add(new TextBlock
        {
            Text = primary,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(secondary) && secondary != primary)
        {
            _rhythmCard.Children.Add(new TextBlock
            {
                Text = secondary,
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        _rhythmCard.Children.Add(new TextBlock
        {
            Text = $"{AppStrings.PathologyLeadsLabel}: {entry.LeadsCount}",
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 0),
        });
        var markers = MarkerSummary();
        if (!string.IsNullOrEmpty(markers))
        {
            _rhythmCard.Children.Add(new TextBlock
            {
                Text = $"{AppStrings.PathologyMarkersLabel}: {markers}",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>Distinct ECG significant-point labels for the selected rhythm, in complex order.</summary>
    private string MarkerSummary()
    {
        var points = _rhythmVm?.SignificantPoints;
        if (points is null || points.Count == 0) return string.Empty;
        var labels = points
            .Select(p => p.Type)
            .Distinct()
            .OrderBy(t => (int)t)
            .Select(t => t.Label().Replace("<sub>", string.Empty).Replace("</sub>", string.Empty));
        return string.Join(", ", labels);
    }

    /// <summary>Re-syncs the current course selection from the top panel (called when re-shown).</summary>
    public void Refresh()
    {
        SyncSelectedCourse();
        UpdateContentArea();
    }
}
