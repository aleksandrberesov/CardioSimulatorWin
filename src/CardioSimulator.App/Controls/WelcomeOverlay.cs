using CardioSimulator.App.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>
/// First-launch welcome screen shown over the Teaching window. A self-branded, full-window
/// onboarding panel: a deep ECG-themed gradient with a faint trace, a short intro + feature list,
/// and a "Start" button. It is intentionally opaque and the main shell is hidden behind it while it
/// shows, because the Teaching monitor (Win2D) and lecture viewer (WebView2) are native airspace
/// surfaces that would otherwise render over a translucent XAML overlay (see
/// <see cref="CourseViewerPanel"/>). Raises <see cref="Started"/> when the user taps "Start".
/// </summary>
public sealed class WelcomeOverlay : UserControl
{
    private static readonly Color Accent = Color.FromArgb(255, 0x5C, 0xE1, 0xC8);

    /// <summary>Raised when the user dismisses the welcome screen with the "Start" button.</summary>
    public event EventHandler? Started;

    public WelcomeOverlay()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // Self-contained branded background, independent of the app light/dark theme so the screen
        // reads as an intentional welcome rather than a panel bleeding through the shell behind it.
        var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x0B, 0x1E, 0x2B), Offset = 0 });
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x10, 0x33, 0x44), Offset = 0.55 });
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x16, 0x4A, 0x52), Offset = 1 });
        Background = bg;

        var root = new Grid();
        root.Children.Add(BuildTrace());

        var content = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 700,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(32),
        };

        content.Children.Add(new FontIcon
        {
            Glyph = char.ConvertFromUtf32(0xE95E), // heart / pulse
            FontSize = 56,
            Foreground = new SolidColorBrush(Accent),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.WelcomeTitle,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.WelcomeBody,
            FontSize = 16,
            LineHeight = 24,
            Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var features = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
        };
        foreach (var feature in AppStrings.WelcomeFeatures)
            features.Children.Add(BuildFeatureRow(feature));
        content.Children.Add(features);

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.WelcomeTagline,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x9F, 0xF0, 0xE2)),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var start = new Button
        {
            Content = AppStrings.WelcomeStart,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(48, 12, 48, 12),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(255, 0x18, 0xC4, 0xA6)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x06, 0x1A, 0x16)),
        };
        start.Click += (_, _) => Started?.Invoke(this, EventArgs.Empty);
        content.Children.Add(start);

        root.Children.Add(content);
        Content = root;

        Loaded += (_, _) => start.Focus(FocusState.Programmatic);
    }

    private static UIElement BuildFeatureRow(string text)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(new FontIcon
        {
            Glyph = char.ConvertFromUtf32(0xE73E), // checkmark
            FontSize = 16,
            Foreground = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>A faint, repeating PQRST trace pinned to the bottom edge as a thematic accent.</summary>
    private static UIElement BuildTrace()
    {
        var points = new PointCollection();
        double x = 0;
        const double mid = 60;
        for (var beat = 0; beat < 16; beat++)
        {
            points.Add(new Point(x, mid)); x += 42;
            points.Add(new Point(x, mid)); x += 8;
            points.Add(new Point(x, mid - 7)); x += 6;  // P wave
            points.Add(new Point(x, mid)); x += 6;
            points.Add(new Point(x, mid + 8)); x += 4;  // Q
            points.Add(new Point(x, mid - 42)); x += 4; // R
            points.Add(new Point(x, mid + 16)); x += 4; // S
            points.Add(new Point(x, mid)); x += 10;
            points.Add(new Point(x, mid - 12)); x += 9; // T wave
            points.Add(new Point(x, mid)); x += 18;
        }

        return new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x5C, 0xE1, 0xC8)),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            IsHitTestVisible = false,
        };
    }
}
