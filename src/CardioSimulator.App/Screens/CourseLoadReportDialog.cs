using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Progress-then-report dialog for an explicit course-pack import. It shows a spinner while the pack
/// loads, then replaces it with a summary of what came through — courses, lecture counts, and a short
/// plain-text preview read from a real lecture. The preview is the point: it makes "loaded but empty"
/// (structure present, no readable content) visible to the user instead of a silent switch.
/// </summary>
public static class CourseLoadReportDialog
{
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xB8, 0x6E, 0x00);

    public static async Task ShowAsync(XamlRoot xamlRoot, AppViewModel appVm, StorageFile file)
    {
        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(LoadingRow(file.Name));

        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseLoadTitle,
            Content = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460,
            },
            CloseButtonText = AppStrings.DataSourceClose,
            XamlRoot = xamlRoot,
        };

        dialog.Opened += async (d, _) =>
        {
            CourseLoadReport report;
            try
            {
                report = await appVm.SetCourseFolderAsync(file);
            }
            catch
            {
                report = new CourseLoadReport(false, file.Name,
                    System.Array.Empty<CourseLoadSummary>(), 0, null, null, null);
            }
            d.Title = report.Success ? AppStrings.CourseLoadTitle : AppStrings.CourseLoadFailedTitle;
            body.Children.Clear();
            Populate(body, report);
        };

        await dialog.ShowAsync();
    }

    private static UIElement LoadingRow(string fileName)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
        row.Children.Add(new TextBlock
        {
            Text = AppStrings.CourseLoadLoadingFormat(fileName),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    private static void Populate(Panel body, CourseLoadReport report)
    {
        body.Children.Add(new TextBlock
        {
            Text = report.FileName,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!report.Success)
        {
            body.Children.Add(new TextBlock
            {
                Text = AppStrings.CourseLoadFailedBody,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        if (report.CourseCount == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = AppStrings.CourseLoadNoCourses,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        body.Children.Add(new TextBlock
        {
            Text = AppStrings.CourseLoadSummaryFormat(report.CourseCount, report.TotalLectures),
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
        });

        if (report.StructureWithoutContent)
            body.Children.Add(WarningBox(AppStrings.CourseLoadEmptyWarning));

        var list = new StackPanel { Spacing = 4 };
        foreach (var course in report.Courses)
        {
            var langs = course.Languages.Count > 0 ? $"   [{string.Join(", ", course.Languages)}]" : "";
            list.Children.Add(new TextBlock
            {
                Text = $"•  {course.Title}  —  {AppStrings.CourseLoadLecturesFormat(course.LectureCount)}{langs}",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        body.Children.Add(list);

        if (!string.IsNullOrEmpty(report.PreviewSnippet))
            body.Children.Add(PreviewBox(report));
    }

    private static UIElement PreviewBox(CourseLoadReport report)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.CourseLoadPreviewHeader,
            FontWeight = FontWeights.SemiBold,
        });

        var crumb = report.PreviewCourseTitle is { } c && report.PreviewLectureTitle is { } l
            ? $"{c}  ›  {l}"
            : report.PreviewLectureTitle ?? report.PreviewCourseTitle ?? "";
        var inner = new StackPanel { Spacing = 4 };
        if (crumb.Length > 0)
            inner.Children.Add(new TextBlock { Text = crumb, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        inner.Children.Add(new TextBlock { Text = report.PreviewSnippet, TextWrapping = TextWrapping.Wrap });

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = inner,
        };
    }

    private static UIElement WarningBox(string message)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xB8, 0x6E, 0x00)),
            BorderBrush = new SolidColorBrush(Amber),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Amber),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }
}
