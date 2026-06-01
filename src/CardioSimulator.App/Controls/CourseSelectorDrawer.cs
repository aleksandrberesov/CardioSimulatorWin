using System.ComponentModel;
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
/// Collapsible right-side drawer that lets the user pick a course + one of its lectures.
/// On selection, fires <see cref="LectureSelected"/> with (courseId, lectureId, language).
/// Port of the Android <c>CourseSelector</c> drawer (Phase 2 of the Course Constructor plan).
/// </summary>
public sealed class CourseSelectorDrawer : UserControl
{
    private static readonly string GlyphLeft = char.ConvertFromUtf32(0xE76B);
    private static readonly string GlyphRight = char.ConvertFromUtf32(0xE76C);

    private readonly StackPanel _coursesList = new() { Spacing = 2 };
    private readonly StackPanel _lecturesList = new() { Spacing = 2, Padding = new Thickness(12, 4, 0, 4) };
    private readonly TextBlock _emptyHint = new()
    {
        Text = "No courses loaded.",
        FontSize = 12,
        Foreground = new SolidColorBrush(Colors.Gray),
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Border _panelHost;
    private readonly FontIcon _handleIcon;
    private bool _expanded;
    private CourseRepository? _repo;
    private CourseViewerViewModel? _viewer;
    private AppViewModel? _appVm;
    private string? _selectedCourseId;

    /// <summary>Fired when the user picks a lecture (courseId, lectureId, language).</summary>
    public event Action<string, string, string>? LectureSelected;

    public CourseSelectorDrawer()
    {
        var stack = new StackPanel { Padding = new Thickness(8), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Courses", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(_emptyHint);
        stack.Children.Add(_coursesList);
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.LightGray), Margin = new Thickness(0, 4, 0, 4) });
        stack.Children.Add(new TextBlock { Text = "Lectures", FontWeight = FontWeights.SemiBold });
        stack.Children.Add(_lecturesList);

        _panelHost = new Border
        {
            Width = 300,
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            Child = new ScrollViewer { Content = stack, VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Visibility = Visibility.Collapsed,
        };

        _handleIcon = new FontIcon
        {
            Glyph = GlyphLeft, // arrow points "into" the screen when collapsed (toward content)
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.Black),
        };
        var handle = new Border
        {
            Width = 24,
            Height = 64,
            Background = new SolidColorBrush(Colors.Gainsboro),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Child = _handleIcon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        handle.Tapped += (_, _) => ToggleExpanded();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        row.Children.Add(handle);
        row.Children.Add(_panelHost);
        Content = row;
    }

    public void Bind(AppViewModel appVm, CourseViewerViewModel viewer)
    {
        _appVm = appVm;
        _viewer = viewer;
        _repo = appVm.CourseRepository;
        _repo.ManifestChanged += (_, _) => Rebuild();
        appVm.PropertyChanged += OnAppChanged;
        Rebuild();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage)) Rebuild();
    }

    private void ToggleExpanded()
    {
        _expanded = !_expanded;
        _panelHost.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _handleIcon.Glyph = _expanded ? GlyphRight : GlyphLeft;
        if (_expanded) Rebuild();
    }

    private void Rebuild()
    {
        _coursesList.Children.Clear();
        _lecturesList.Children.Clear();
        if (_repo is null) return;

        var courses = _repo.Courses;
        _emptyHint.Visibility = courses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var c in courses)
        {
            var captured = c;
            var label = _appVm?.SelectedLanguage == DomainLanguage.RU ? (c.NameRu ?? c.TitleEn) : c.TitleEn;
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontWeight = c.Id == _selectedCourseId ? FontWeights.SemiBold : FontWeights.Normal,
            };
            btn.Click += (_, _) =>
            {
                _selectedCourseId = captured.Id;
                ShowLecturesFor(captured.Id);
                Rebuild(); // refresh course bold state
            };
            _coursesList.Children.Add(btn);
        }

        if (_selectedCourseId is not null) ShowLecturesFor(_selectedCourseId);
    }

    private void ShowLecturesFor(string courseId)
    {
        _lecturesList.Children.Clear();
        if (_repo is null || _appVm is null) return;
        var course = _repo.ReadCourse(courseId);
        if (course is null) return;
        var langTag = _appVm.SelectedLanguage.Tag();
        foreach (var lec in course.Lectures)
        {
            var captured = lec;
            var label = _appVm.SelectedLanguage == DomainLanguage.RU ? (lec.NameRu ?? lec.TitleEn) : lec.TitleEn;
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 13,
            };
            btn.Click += (_, _) =>
            {
                _viewer?.SelectCourse(courseId);
                _viewer?.SelectLecture(captured.Id, langTag);
                LectureSelected?.Invoke(courseId, captured.Id, langTag);
            };
            _lecturesList.Children.Add(btn);
        }
    }
}
