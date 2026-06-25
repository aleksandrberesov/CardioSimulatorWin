using CardioSimulator.App.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CardioSimulator.App.Controls;

/// <summary>
/// "ЭОС" (electrical axis) window: a semi-transparent panel docked to the right edge of the monitor,
/// overlaying the live trace. Implemented as a <see cref="Popup"/> so it composites above the native
/// Win2D monitor surface, and its translucent blue fill lets the ECG show through. Shows the axis
/// vectors, a hexaxial diagram, and a result line. Toggled by the panel's EOS button. Currently a
/// scaffold — the vector computation and ECG highlighting land in a later increment.
/// </summary>
public static class EosWindow
{
    private const double PanelWidth = 280;

    private static readonly SolidColorBrush PanelFill =
        new(new Windows.UI.Color { A = 0xCC, R = 0x5B, G = 0x9B, B = 0xD5 });
    private static readonly SolidColorBrush White = new(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush DiagramBg = new(new Windows.UI.Color { A = 0xF2, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush Axis = new(new Windows.UI.Color { A = 255, R = 0x99, G = 0x99, B = 0x99 });
    private static readonly SolidColorBrush Vector1 = new(new Windows.UI.Color { A = 255, R = 0xD8, G = 0x3A, B = 0x3A });
    private static readonly SolidColorBrush Vector2 = new(new Windows.UI.Color { A = 255, R = 0x1E, G = 0x5F, B = 0xA5 });

    private static Popup? _popup;

    /// <summary>Opens the EOS panel on the right of the monitor, or closes it if already open.</summary>
    public static void Toggle(XamlRoot xamlRoot)
    {
        if (_popup is { IsOpen: true })
        {
            Close();
            return;
        }
        Open(xamlRoot);
    }

    /// <summary>Closes the panel if open (e.g. when leaving the monitor).</summary>
    public static void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
    }

    private static void Open(XamlRoot xamlRoot)
    {
        var size = xamlRoot.Size;
        const double topMargin = 72;    // clears the top mode bar
        const double bottomMargin = 72; // clears the bottom control panel
        const double rightMargin = 16;
        var height = Math.Max(220, size.Height - topMargin - bottomMargin);

        _popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = BuildPanel(height),
            HorizontalOffset = Math.Max(0, size.Width - PanelWidth - rightMargin),
            VerticalOffset = topMargin,
            IsLightDismissEnabled = false,
        };
        _popup.IsOpen = true;
    }

    private static UIElement BuildPanel(double height)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Title(AppStrings.MonitorEosWindowTitle));
        content.Children.Add(Line($"1.  {AppStrings.MonitorEosVectorFormat(1)}"));
        content.Children.Add(Line($"2.  {AppStrings.MonitorEosVectorFormat(2)}"));
        content.Children.Add(Note(AppStrings.MonitorEosNote));
        content.Children.Add(Hexaxial());
        content.Children.Add(new TextBlock
        {
            Text = AppStrings.MonitorEosResult,
            Foreground = White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Close affordance pinned to the top-right corner.
        var close = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel) { Foreground = White },
            Background = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 }),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => Close();

        var grid = new Grid();
        grid.Children.Add(scroller);
        grid.Children.Add(close);

        return new Border
        {
            Width = PanelWidth,
            Height = height,
            Background = PanelFill,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 12, 16, 16),
            Child = grid,
        };
    }

    private static TextBlock Title(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>A small hexaxial reference circle with two axis vectors — placeholder for the
    /// computed electrical axis.</summary>
    private static UIElement Hexaxial()
    {
        const double s = 180;       // canvas size
        const double c = s / 2;     // center
        const double r = 78;        // radius

        var canvas = new Canvas { Width = s, Height = s };
        canvas.Children.Add(new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Stroke = Axis,
            StrokeThickness = 1,
            Margin = new Thickness(c - r, c - r, 0, 0),
        });

        // Six hexaxial spokes (0°, ±30°, ±60°, 90° … approximated by 0/30/60/90/120/150).
        for (var deg = 0; deg < 180; deg += 30)
            canvas.Children.Add(Spoke(c, r, deg, Axis, 0.8));

        // Two illustrative vectors (rays from the center).
        canvas.Children.Add(Ray(c, r * 0.95, 30, Vector1, 3));   // vector 1
        canvas.Children.Add(Ray(c, r * 0.8, 110, Vector2, 3));   // vector 2

        return new Border
        {
            Background = DiagramBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas,
        };
    }

    // A full diameter line at the given angle (used for the hexaxial axes).
    private static Line Spoke(double center, double radius, double degrees, Brush brush, double thickness)
    {
        var rad = degrees * Math.PI / 180.0;
        var dx = radius * Math.Cos(rad);
        var dy = radius * Math.Sin(rad);
        return new Line
        {
            X1 = center - dx,
            Y1 = center + dy,
            X2 = center + dx,
            Y2 = center - dy,
            Stroke = brush,
            StrokeThickness = thickness,
        };
    }

    // A ray from the center outward at the given angle (used for the vectors).
    private static Line Ray(double center, double radius, double degrees, Brush brush, double thickness)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Line
        {
            X1 = center,
            Y1 = center,
            X2 = center + radius * Math.Cos(rad),
            Y2 = center - radius * Math.Sin(rad),
            Stroke = brush,
            StrokeThickness = thickness,
        };
    }
}
