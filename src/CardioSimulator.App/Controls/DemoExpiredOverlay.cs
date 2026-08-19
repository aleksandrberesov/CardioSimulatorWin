using System.Globalization;
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
/// Full-window block shown when a time-limited demo build has passed its expiry (see
/// <see cref="DemoGuard"/>). Deliberately opaque and terminal: it replaces the whole shell, offers no
/// way into the app, and its only action is Exit. Styled like <see cref="WelcomeOverlay"/> (branded
/// dark gradient + faint ECG trace) but with an alert-red accent so it reads as a stop, not a welcome.
/// Raises <see cref="ExitRequested"/> when the user clicks Exit.
/// </summary>
public sealed class DemoExpiredOverlay : UserControl
{
    // Shared alert red used across the app for "stop / deviation" highlights.
    private static readonly Color Alert = Color.FromArgb(255, 0xD3, 0x3A, 0x2F);

    /// <summary>Raised when the user dismisses the block with the "Exit" button.</summary>
    public event EventHandler? ExitRequested;

    public DemoExpiredOverlay(DemoStatus status)
    {
        RequestedTheme = ElementTheme.Dark;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x1A, 0x0E, 0x0E), Offset = 0 });
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x24, 0x12, 0x12), Offset = 0.55 });
        bg.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x33, 0x18, 0x16), Offset = 1 });
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
            Glyph = char.ConvertFromUtf32(0xE7BA), // warning triangle
            FontSize = 56,
            Foreground = new SolidColorBrush(Alert),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.DemoExpiredTitle,
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.DemoExpiredBody,
            FontSize = 16,
            LineHeight = 24,
            Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.DemoExpiredContact,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 24,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xB8, 0xB2)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        content.Children.Add(new TextBlock
        {
            Text = AppStrings.DemoExpiredDates(Fmt(status.BuildDate), Fmt(status.ExpiryDate)),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var exit = new Button
        {
            Content = AppStrings.DemoExpiredExit,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(48, 12, 48, 12),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Alert),
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 12, 0, 0),
        };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        content.Children.Add(exit);

        root.Children.Add(content);
        Content = root;

        Loaded += (_, _) => exit.Focus(FocusState.Programmatic);
    }

    private static string Fmt(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A faint, flat-lining PQRST trace pinned to the bottom edge — it fades to a flatline
    /// on the right as a thematic nod to the demo having "ended".</summary>
    private static UIElement BuildTrace()
    {
        var points = new PointCollection();
        double x = 0;
        const double mid = 60;
        // A few beats, then flatline.
        for (var beat = 0; beat < 6; beat++)
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
        points.Add(new Point(x + 620, mid)); // flatline to the edge

        return new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xD3, 0x3A, 0x2F)),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            IsHitTestVisible = false,
        };
    }
}
