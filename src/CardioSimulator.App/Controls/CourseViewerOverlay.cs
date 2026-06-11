using System.ComponentModel;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Full-screen course-viewer overlay opened from the Teaching screen's School button. Hosts a
/// persistent <see cref="LectureWebView"/> plus a left-edge collapsible Lectures drawer. Course
/// selection is handled by the top panel's course selector.
/// </summary>
public sealed class CourseViewerOverlay : UserControl
{
    private readonly LectureWebView _web = new();
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _placeholder = new()
    {
        Text = "Select a lecture",
        Foreground = new SolidColorBrush(Colors.Gray),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel _lecturesList = new() { Spacing = 2, Padding = new Thickness(8) };

    private Border _lecturesPanel = null!;
    private bool _lecturesExpanded;

    private AppViewModel? _appVm;
    private CourseViewerViewModel? _viewer;
    private CourseRepository? _repo;
    private string? _selectedCourseId;

    /// <summary>Raised when the overlay's close button is tapped.</summary>
    public event EventHandler? Closed;

    public CourseViewerOverlay()
    {
        Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xFA, G = 0xFA, B = 0xFA });
        Visibility = Visibility.Collapsed;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: title + close.
        var topBar = new Grid { Height = 56, Padding = new Thickness(16, 0, 8, 0), Background = new SolidColorBrush(Colors.WhiteSmoke) };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        topBar.Children.Add(_title);
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 1);
        topBar.Children.Add(close);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        // Content: web view (or placeholder) + lectures drawer.
        var content = new Grid();
        content.Children.Add(_web);
        content.Children.Add(_placeholder);
        _web.Visibility = Visibility.Collapsed;

        var lecturesScroll = new ScrollViewer { Content = _lecturesList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(BuildLecturesDrawer(lecturesScroll));

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        Content = root;
    }

    private Border BuildLecturesDrawer(UIElement panelContent)
    {
        _lecturesPanel = new Border
        {
            Width = 300,
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            Child = panelContent,
            Visibility = Visibility.Collapsed,
        };

        // Width=80 lets "Lectures" render fully; negative left/right margins collapse the layout
        // footprint back to the handle's 24 px so the Border doesn't grow wider.
        var handleLabel = new TextBlock
        {
            Text = "Lectures",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Black),
            Width = 80,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-28, 0, -28, 0),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = -90 },
        };
        var handle = new Border
        {
            Width = 24,
            MinHeight = 96,
            Background = new SolidColorBrush(Colors.Gainsboro),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = handleLabel,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
        };
        handle.Tapped += (_, _) =>
        {
            _lecturesExpanded = !_lecturesExpanded;
            _lecturesPanel.Visibility = _lecturesExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (_lecturesExpanded) RebuildLectures();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(_lecturesPanel);
        row.Children.Add(handle);
        return new Border { Child = row, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
    }

    public void Bind(AppViewModel appVm, CourseViewerViewModel viewer)
    {
        _appVm = appVm;
        _viewer = viewer;
        _repo = appVm.CourseRepository;
        viewer.PropertyChanged += OnViewerChanged;
        appVm.PropertyChanged += OnAppChanged;
        SyncSelectedCourse();
        UpdateTitle();
    }

    private void SyncSelectedCourse()
    {
        if (_appVm is null) return;
        var newId = _appVm.SelectedCourseId;
        if (_selectedCourseId == newId) return;
        _selectedCourseId = newId;
        if (_selectedCourseId is not null)
            _viewer?.SelectCourse(_selectedCourseId);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage))
        {
            RebuildLectures();
            UpdateTitle();
        }
        else if (e.PropertyName == nameof(AppViewModel.SelectedCourseId))
        {
            _lecturesExpanded = false;
            _lecturesPanel.Visibility = Visibility.Collapsed;
            SyncSelectedCourse();
            UpdateTitle();
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
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_appVm is null) { _title.Text = "Courses"; return; }
        var lectureTitle = _viewer?.SelectedLecture is { } lec
            ? (_appVm.SelectedLanguage == DomainLanguage.RU ? lec.NameRu ?? lec.TitleEn : lec.TitleEn)
            : null;
        if (!string.IsNullOrWhiteSpace(lectureTitle)) { _title.Text = lectureTitle!; return; }
        var course = _selectedCourseId is null ? null : _repo?.ReadCourse(_selectedCourseId);
        _title.Text = course is null
            ? "Courses"
            : (_appVm.SelectedLanguage == DomainLanguage.RU ? course.NameRu ?? course.TitleEn : course.TitleEn);
    }

    private void RebuildLectures()
    {
        _lecturesList.Children.Clear();
        if (_repo is null || _appVm is null || _selectedCourseId is null) return;
        var course = _repo.ReadCourse(_selectedCourseId);
        if (course is null) return;
        var langTag = _appVm.SelectedLanguage.Tag();
        foreach (var lec in course.Lectures)
        {
            var captured = lec;
            var label = _appVm.SelectedLanguage == DomainLanguage.RU ? lec.NameRu ?? lec.TitleEn : lec.TitleEn;
            var selected = _viewer?.SelectedLecture?.Id == lec.Id;
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            btn.Click += (_, _) =>
            {
                _viewer?.SelectLecture(captured.Id, langTag);
                _lecturesExpanded = false;
                _lecturesPanel.Visibility = Visibility.Collapsed;
                RebuildLectures();
            };
            _lecturesList.Children.Add(btn);
        }
    }

    /// <summary>Shows the overlay and syncs the current course selection from the top panel.</summary>
    public void Open()
    {
        SyncSelectedCourse();
        UpdateTitle();
        Visibility = Visibility.Visible;
    }
}
