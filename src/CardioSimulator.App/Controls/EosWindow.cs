using System.Globalization;
using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CardioSimulator.App.Controls;

/// <summary>
/// "ЭОС" (electrical axis) window: a semi-transparent panel docked to the right edge of the monitor,
/// overlaying the live trace. Implemented as a <see cref="Popup"/> so it composites above the native
/// Win2D monitor surface, and its translucent blue fill lets the ECG show through. It is an on-screen
/// reference that walks the user through determining the electrical axis: a numbered 7-step method,
/// a coordinate diagram of the I/aVF construction (red vector a on I, green vector b on aVF, blue
/// resultant), and the list of axis-deviation variants with their angle ranges. Toggled by the panel's
/// EOS button.
/// </summary>
public static class EosWindow
{
    private const double PanelWidth = 280;

    private static readonly SolidColorBrush PanelFill =
        new(new Windows.UI.Color { A = 0xCC, R = 0x5B, G = 0x9B, B = 0xD5 });
    private static readonly SolidColorBrush White = new(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush DiagramBg = new(new Windows.UI.Color { A = 0xF2, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush Axis = new(new Windows.UI.Color { A = 255, R = 0x99, G = 0x99, B = 0x99 });
    private static readonly SolidColorBrush AxisMain = new(new Windows.UI.Color { A = 255, R = 0x44, G = 0x44, B = 0x44 });
    private static readonly SolidColorBrush VectorA = new(new Windows.UI.Color { A = 255, R = 0xD8, G = 0x3A, B = 0x3A }); // I  – red
    private static readonly SolidColorBrush VectorB = new(new Windows.UI.Color { A = 255, R = 0x2E, G = 0x8B, B = 0x3A }); // aVF – green
    private static readonly SolidColorBrush Resultant = new(new Windows.UI.Color { A = 255, R = 0x1E, G = 0x5F, B = 0xA5 }); // α – blue

    private static Popup? _popup;
    private static XamlRoot? _xamlRoot;

    /// <summary>True while the panel is showing.</summary>
    public static bool IsOpen => _popup is { IsOpen: true };

    /// <summary>
    /// Opens the EOS panel on the right of the monitor (or closes it if already open), showing the
    /// axis computed from the current ECG. Pass <c>null</c> when no rhythm/QRS is available — the
    /// panel then shows the method and a "no data" note instead of measured values.
    /// </summary>
    /// <returns><c>true</c> if the panel is now open, <c>false</c> if this toggle closed it.</returns>
    public static bool Toggle(XamlRoot xamlRoot, EosResult? result)
    {
        if (_popup is { IsOpen: true })
        {
            Close();
            return false;
        }
        Open(xamlRoot, result);
        return true;
    }

    /// <summary>
    /// Rebuilds the open panel with a freshly computed axis; a no-op when the panel is closed. Used
    /// to keep the window in sync when the selected pathology changes while it is showing.
    /// </summary>
    public static void Update(EosResult? result)
    {
        if (_popup is not { IsOpen: true } || _xamlRoot is null) return;
        _popup.Child = BuildPanel(PanelHeight(_xamlRoot), result);
    }

    /// <summary>Closes the panel if open (e.g. when leaving the monitor).</summary>
    public static void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
        _xamlRoot = null;
    }

    private static void Open(XamlRoot xamlRoot, EosResult? result)
    {
        _xamlRoot = xamlRoot;
        var size = xamlRoot.Size;
        const double topMargin = 72;    // clears the top mode bar
        const double rightMargin = 16;

        _popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = BuildPanel(PanelHeight(xamlRoot), result),
            HorizontalOffset = Math.Max(0, size.Width - PanelWidth - rightMargin),
            VerticalOffset = topMargin,
            IsLightDismissEnabled = false,
        };
        _popup.IsOpen = true;
    }

    // Panel height fills the monitor between the top mode bar and the bottom control panel.
    private static double PanelHeight(XamlRoot xamlRoot)
    {
        const double topMargin = 72;
        const double bottomMargin = 72;
        return Math.Max(220, xamlRoot.Size.Height - topMargin - bottomMargin);
    }

    private static UIElement BuildPanel(double height, EosResult? result)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Title(AppStrings.MonitorEosWindowTitle));
        content.Children.Add(Intro(AppStrings.MonitorEosIntro));

        for (var i = 1; i <= 7; i++)
            content.Children.Add(Step(i, AppStrings.MonitorEosStep(i)));

        content.Children.Add(Diagram(result));
        content.Children.Add(Measured(result));

        content.Children.Add(VariantsHeader(AppStrings.MonitorEosVariantsHeader));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantNormal, result?.AxisClass == EosAxisClass.Normal));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantHorizontal, result?.AxisClass == EosAxisClass.Horizontal));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantVertical, result?.AxisClass == EosAxisClass.Vertical));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantLeft, result?.AxisClass == EosAxisClass.LeftDeviation));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantRight, result?.AxisClass == EosAxisClass.RightDeviation));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantExtreme, result?.AxisClass == EosAxisClass.ExtremeDeviation));

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

    private static TextBlock Intro(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
    };

    /// <summary>A numbered method step: the number sits in a fixed gutter, the wrapped text beside it.</summary>
    private static UIElement Step(int number, string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var num = new TextBlock
        {
            Text = $"{number}.",
            Foreground = White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(num, 0);

        var body = new TextBlock
        {
            Text = text,
            Foreground = White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(body, 1);

        grid.Children.Add(num);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock VariantsHeader(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };

    /// <summary>An axis-variant row: the leading name (up to the first colon) is emphasized, the
    /// angle range follows in the regular weight. The <paramref name="active"/> row (the computed
    /// axis) is boldened whole and wrapped in a translucent pill.</summary>
    private static UIElement Variant(string text, bool active)
    {
        var tb = new TextBlock
        {
            Foreground = White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        var nameWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
        // Accept both ASCII and full-width (CJK) colons so the split works across locales.
        var sep = text.IndexOfAny(new[] { ':', '：' });
        if (sep > 0)
        {
            tb.Inlines.Add(new Run { Text = text[..(sep + 1)], FontWeight = nameWeight });
            tb.Inlines.Add(new Run { Text = text[(sep + 1)..], FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal });
        }
        else
        {
            tb.Text = text;
            tb.FontWeight = nameWeight;
        }
        if (!active) return tb;

        return new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 0x40, R = 255, G = 255, B = 255 }),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3, 6, 3),
            Child = tb,
        };
    }

    /// <summary>The computed readout (measured q/R/S for I and aVF, the α angle and its band), or a
    /// short "no data" note when the axis could not be determined from the current ECG.</summary>
    private static UIElement Measured(EosResult? result)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var box = new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 0x33, R = 255, G = 255, B = 255 }),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = panel,
        };

        if (result is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = AppStrings.MonitorEosNoData,
                Foreground = White,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
            return box;
        }

        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.MonitorEosMeasuredHeader,
            Foreground = White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(ReadoutLine(AppStrings.MonitorEosLeadFormat(
            "I", Mm(result.LeadI.QMm), Mm(result.LeadI.RMm), Mm(result.LeadI.SMm), "a", Mm(result.LeadI.NetMm))));
        panel.Children.Add(ReadoutLine(AppStrings.MonitorEosLeadFormat(
            "aVF", Mm(result.LeadAvf.QMm), Mm(result.LeadAvf.RMm), Mm(result.LeadAvf.SMm), "b", Mm(result.LeadAvf.NetMm))));
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.MonitorEosAngleFormat(
                result.AngleDeg.ToString("0", CultureInfo.CurrentCulture), VariantName(result.AxisClass)),
            Foreground = White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return box;
    }

    private static TextBlock ReadoutLine(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Mm(double value) => value.ToString("0.0", CultureInfo.CurrentCulture);

    // The localized variant name (the part before the colon of the corresponding variant string).
    private static string VariantName(EosAxisClass axisClass)
    {
        var text = axisClass switch
        {
            EosAxisClass.Normal => AppStrings.MonitorEosVariantNormal,
            EosAxisClass.Horizontal => AppStrings.MonitorEosVariantHorizontal,
            EosAxisClass.Vertical => AppStrings.MonitorEosVariantVertical,
            EosAxisClass.LeftDeviation => AppStrings.MonitorEosVariantLeft,
            EosAxisClass.RightDeviation => AppStrings.MonitorEosVariantRight,
            _ => AppStrings.MonitorEosVariantExtreme,
        };
        var sep = text.IndexOfAny(new[] { ':', '：' });
        return sep > 0 ? text[..sep] : text;
    }

    /// <summary>The I/aVF coordinate construction: red vector a on the horizontal (I) axis, green
    /// vector b on the vertical (aVF) axis, dashed perpendiculars closing the rectangle, and the blue
    /// resultant (the α angle). Driven by the computed net QRS of I (a) and aVF (b); when no result is
    /// available it falls back to illustrative values (a=2, b=6) that mirror the worked example.</summary>
    private static UIElement Diagram(EosResult? result)
    {
        const double s = 190;       // canvas size
        const double c = s / 2;     // center
        const double r = 74;        // reference-circle radius

        double a = result?.LeadI.NetMm ?? 2;    // vector a on lead I  (R-(q+S))
        double b = result?.LeadAvf.NetMm ?? 6;  // vector b on lead aVF

        // Scale so the longer projection reaches ~85% of the radius; guard tiny/zero magnitudes.
        double maxAbs = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1e-3);
        double unit = r * 0.85 / maxAbs;

        double ax = c + a * unit;   // tip of a on the I axis (sign → left/right)
        double by = c + b * unit;   // tip of b on the aVF axis (sign → up/down)

        var canvas = new Canvas { Width = s, Height = s };

        canvas.Children.Add(new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Stroke = Axis,
            StrokeThickness = 1,
            Margin = new Thickness(c - r, c - r, 0, 0),
        });

        // Faint hexaxial spokes (0/30/60/90/120/150) for reference.
        for (var deg = 0; deg < 180; deg += 30)
            canvas.Children.Add(Spoke(c, r, deg, Axis, 0.8));

        // Emphasize the two working axes: I (horizontal) and aVF (vertical).
        canvas.Children.Add(Spoke(c, r, 0, AxisMain, 1.4));
        canvas.Children.Add(Spoke(c, r, 90, AxisMain, 1.4));

        // Rectangle construction: perpendiculars dropped from each vector tip.
        canvas.Children.Add(Dashed(ax, c, ax, by));
        canvas.Children.Add(Dashed(c, by, ax, by));

        // Vectors: a along I (red), b along aVF (green), resultant to the corner (blue).
        canvas.Children.Add(Ray(c, c, ax, c, VectorA, 3));
        canvas.Children.Add(Ray(c, c, c, by, VectorB, 3));
        canvas.Children.Add(Ray(c, c, ax, by, Resultant, 3));

        // Axis and vector labels.
        canvas.Children.Add(Label(c + r - 8, c + 2, "I", AxisMain));
        canvas.Children.Add(Label(c + 3, c + r - 4, "aVF", AxisMain));
        canvas.Children.Add(Label(ax + 3, c - 15, "a", VectorA));
        canvas.Children.Add(Label(c - 12, by - 8, "b", VectorB));
        canvas.Children.Add(Label(c + 8, c + 6, "α", Resultant));

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

    // A straight segment between two canvas points (used for the vectors).
    private static Line Ray(double x1, double y1, double x2, double y2, Brush brush, double thickness) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = brush,
        StrokeThickness = thickness,
    };

    // A dashed perpendicular used to close the construction rectangle.
    private static Line Dashed(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = AxisMain,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 3, 3 },
    };

    // A small text marker placed at an absolute canvas position.
    private static TextBlock Label(double left, double top, string text, Brush brush)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        return tb;
    }
}
