using System.Globalization;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
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
    private const double PanelWidth = 348;

    private static readonly SolidColorBrush PanelFill =
        new(new Windows.UI.Color { A = 0xCC, R = 0x5B, G = 0x9B, B = 0xD5 });
    private static readonly SolidColorBrush White = new(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush DiagramBg = new(new Windows.UI.Color { A = 0xF2, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush Axis = new(new Windows.UI.Color { A = 255, R = 0x99, G = 0x99, B = 0x99 });
    private static readonly SolidColorBrush AxisMain = new(new Windows.UI.Color { A = 255, R = 0x44, G = 0x44, B = 0x44 });
    private static readonly SolidColorBrush VectorA = new(new Windows.UI.Color { A = 255, R = 0xD8, G = 0x3A, B = 0x3A }); // I  – red
    private static readonly SolidColorBrush VectorB = new(new Windows.UI.Color { A = 255, R = 0x2E, G = 0x8B, B = 0x3A }); // aVF – green
    private static readonly SolidColorBrush Resultant = new(new Windows.UI.Color { A = 255, R = 0x1E, G = 0x5F, B = 0xA5 }); // α – blue
    // Alert red for the abnormal (deviation) axis classes. Per the customer's guidance the panel stays
    // in the calm blue style and red appears ONLY to flag a deviation from the norm — and never as a
    // text colour, because saturated red text on the blue panel reads poorly. It is used solely as a
    // solid highlight pill behind white text. The hue matches the app's electrode-fault alert red
    // (MonitorControlPanel) so the EOS window shares the app's single "out of range" signal.
    private static readonly SolidColorBrush DeviationFill =
        new(new Windows.UI.Color { A = 0xF0, R = 0xD3, G = 0x3A, B = 0x2F });
    // Dark ink for the "how to determine the axis" method flyout (readable on its light background).
    private static readonly SolidColorBrush Ink = new(new Windows.UI.Color { A = 255, R = 0x22, G = 0x2B, B = 0x33 });

    private static Popup? _popup;
    private static XamlRoot? _xamlRoot;
    private static Action? _onClosed;

    /// <summary>True while the panel is showing.</summary>
    public static bool IsOpen => _popup is { IsOpen: true };

    /// <summary>
    /// Opens the EOS panel on the right of the monitor (or closes it if already open), showing the
    /// axis computed from the current ECG. Pass <c>null</c> when no rhythm/QRS is available — the
    /// panel then shows the method and a "no data" note instead of measured values.
    /// </summary>
    /// <param name="onClosed">Invoked whenever the panel closes — including via its own ✕ button —
    /// so the host can un-highlight the EOS tab and clear the trace overlay.</param>
    /// <returns><c>true</c> if the panel is now open, <c>false</c> if this toggle closed it.</returns>
    public static bool Toggle(XamlRoot xamlRoot, EosResult? result, Action? onClosed = null)
    {
        if (_popup is { IsOpen: true })
        {
            Close();
            return false;
        }
        Open(xamlRoot, result, onClosed);
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

    /// <summary>Closes the panel if open (e.g. when leaving the monitor), then fires the close
    /// callback registered at open time.</summary>
    public static void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
        _xamlRoot = null;
        var cb = _onClosed;
        _onClosed = null;
        cb?.Invoke();
    }

    private static void Open(XamlRoot xamlRoot, EosResult? result, Action? onClosed)
    {
        _xamlRoot = xamlRoot;
        _onClosed = onClosed;
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
        // The step-by-step method (old "block 1") moved out of the always-on panel and behind the
        // top-left "(!)" info icon; the panel now leads straight into the labelled diagram.
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Title(AppStrings.MonitorEosWindowTitle));
        content.Children.Add(Diagram(result));
        content.Children.Add(Measured(result));

        content.Children.Add(VariantsHeader(AppStrings.MonitorEosVariantsHeader));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantNormal, result?.AxisClass == EosAxisClass.Normal, deviation: false));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantHorizontal, result?.AxisClass == EosAxisClass.Horizontal, deviation: false));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantVertical, result?.AxisClass == EosAxisClass.Vertical, deviation: false));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantLeft, result?.AxisClass == EosAxisClass.LeftDeviation, deviation: true));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantRight, result?.AxisClass == EosAxisClass.RightDeviation, deviation: true));
        content.Children.Add(Variant(AppStrings.MonitorEosVariantExtreme, result?.AxisClass == EosAxisClass.ExtremeDeviation, deviation: true));

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

        // Info "(!)" affordance pinned to the top-left corner: opens the step-by-step method flyout.
        var info = new Button
        {
            Content = new FontIcon
            {
                Glyph = "", // circled "i"
                Foreground = White,
                FontSize = 16,
            },
            Background = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 }),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(info, AppStrings.MonitorEosInfoTitle);
        info.Click += (_, _) => ShowMethodFlyout(info);

        var grid = new Grid();
        grid.Children.Add(scroller);
        grid.Children.Add(info);
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

    /// <summary>Opens the "how to determine the axis" method flyout (the old block-1 content): the
    /// intro line plus the numbered 7-step method, on a light card anchored to the "(!)" icon.</summary>
    private static void ShowMethodFlyout(FrameworkElement anchor)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(4), Width = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.MonitorEosIntro,
            Foreground = Ink,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        });
        for (var i = 1; i <= 7; i++)
            panel.Children.Add(InfoStep(i, AppStrings.MonitorEosStep(i)));

        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 440,
            },
            Placement = FlyoutPlacementMode.Bottom,
            // Force a light card: the default presenter follows the OS theme and renders near-black in
            // dark mode, hiding the dark text. Pin it to the app's white panel palette instead.
            FlyoutPresenterStyle = LightFlyoutStyle(),
        };
        flyout.ShowAt(anchor);
    }

    // A FlyoutPresenter style that pins the flyout to the app's light panel palette (white fill, dark
    // border) regardless of the OS theme, so the method text stays readable.
    private static Style LightFlyoutStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, AppTheme.PanelBackground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, AppTheme.ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        return style;
    }

    /// <summary>A numbered method step for the info flyout: the number sits in a fixed gutter, the
    /// wrapped text beside it, in dark ink for the light flyout background.</summary>
    private static UIElement InfoStep(int number, string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var num = new TextBlock
        {
            Text = $"{number}.",
            Foreground = Ink,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(num, 0);

        var body = new TextBlock
        {
            Text = text,
            Foreground = Ink,
            FontSize = 13,
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
    /// axis) is boldened whole and wrapped in a translucent pill. Rows are white; a <paramref
    /// name="deviation"/> row (an abnormal deviation axis) turns its pill red when it is the active
    /// reading so the current abnormal axis stands out as an alert.</summary>
    private static UIElement Variant(string text, bool active, bool deviation)
    {
        // All variant rows use white text; the abnormal (deviation) axes are never red-inked, so an
        // active deviation axis is signalled only by the red pill behind the row.
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
            Background = deviation
                ? DeviationFill
                : new SolidColorBrush(new Windows.UI.Color { A = 0x40, R = 255, G = 255, B = 255 }),
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
        // The α readout: white text for legibility in every case. A normal/borderline axis sits in the
        // plain blue style; an abnormal (deviation) axis is flagged by wrapping the readout in the red
        // alert pill — highlighting the deviation without the poorly-readable red text it replaces.
        var angle = new TextBlock
        {
            Text = AppStrings.MonitorEosAngleFormat(
                result.AngleDeg.ToString("0", CultureInfo.CurrentCulture), VariantName(result.AxisClass)),
            Foreground = White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        if (IsDeviation(result.AxisClass))
        {
            panel.Children.Add(new Border
            {
                Background = DeviationFill,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 4, 0, 0),
                Child = angle,
            });
        }
        else
        {
            angle.Margin = new Thickness(0, 2, 0, 0);
            panel.Children.Add(angle);
        }
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

    // The abnormal axis classes (deviations from the norm) — the only rows/readouts highlighted in red.
    private static bool IsDeviation(EosAxisClass axisClass) =>
        axisClass is EosAxisClass.LeftDeviation or EosAxisClass.RightDeviation or EosAxisClass.ExtremeDeviation;

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
        const double s = 296;       // canvas size (~1.55× the original, per customer request)
        const double c = s / 2;     // center
        const double r = 95;        // reference-circle radius (shrunk from the single-label layout to
                                    // free the outer rings for the full hexaxial labels + sector captions)
        const double tipR = r + 17;    // radius of the per-spoke angle/lead tip captions, just outside the rim
        const double arrowR = r + 35;  // radius of the sector sweep-arrows
        const double captionR = r + 45; // radius the curved sector captions follow

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

        // Full hexaxial reference ring, as on the teaching slide: every 30° spoke tip is labelled with
        // its frontal-plane angle over the limb lead (and pole, +/-) that points that way. Canvas angles
        // run 0°→right (+I) and +90°→down (+aVF); each lead's negative pole sits 180° opposite. The two
        // working leads +I and +aVF are tinted to match the red (a) and green (b) construction vectors.
        foreach (var (deg, grad, lead, tint) in HexaxialTips)
            canvas.Children.Add(TipLabel(c, tipR, deg, grad, lead, tint));

        // Sector sweep-arrows: two flows springing from the horizontal +I axis (0°), as on the teaching
        // slide — one curving down/clockwise through the normal (bottom-right) into right deviation
        // (bottom-left), the other up/counter-clockwise through left deviation (top-right) into extreme
        // deviation (top-left). Each arc carries an arrowhead at its far end, showing the axis rotating
        // away from horizontal as the deviation grows. Faint axis grey, hugging the ring.
        SweepArc(canvas, c, arrowR, 8, 82);      // normal → clockwise/down
        SweepArc(canvas, c, arrowR, 98, 172);    // right deviation → clockwise/left
        SweepArc(canvas, c, arrowR, -8, -82);    // left deviation → counter-clockwise/up
        SweepArc(canvas, c, arrowR, -98, -172);  // extreme deviation → counter-clockwise/left

        // Top boundary double-arrow: a single straight ◄─► spanning the -90°/-aVF divide (between left
        // and extreme deviation), connected across as on the concept slide. Sits just above the gap
        // between the two upper sweep-arcs, clear of the -aVF tip label.
        TopBoundaryArrow(canvas, c, c - (arrowR + 6), 22);

        // Sector captions ringing the circle, curved to follow the rim as on the teaching slide: which
        // axis-position zone the α angle falls into. Centred on each quadrant's diagonal — lower-right
        // (0°→+90°) normal, lower-left (+90°→180°) right deviation, upper-right (0°→-90°) left deviation,
        // upper-left (-90°→-180°) extreme ("sharp") deviation. Faint axis grey, framing the construction.
        CurvedCaption(canvas, c, captionR, 45, AppStrings.MonitorEosSectorNormal);
        CurvedCaption(canvas, c, captionR, 135, AppStrings.MonitorEosSectorRight);
        CurvedCaption(canvas, c, captionR, -45, AppStrings.MonitorEosSectorLeft);
        CurvedCaption(canvas, c, captionR, -135, AppStrings.MonitorEosSectorExtreme);

        // Vector labels near each tip: a on I (red), b on aVF (green), α at the origin (blue).
        canvas.Children.Add(Label(ax + 4, c - 18, "a", VectorA));
        canvas.Children.Add(Label(c - 16, by - 9, "b", VectorB));
        canvas.Children.Add(Label(c + 10, c + 8, "α", Resultant));

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

    // The 12 hexaxial spoke tips: (canvas angle°, angle caption, signed limb lead, lead-name colour).
    // Positive poles: I 0°, II +60°, III +120°, aVF +90°, aVL -30°, aVR -150°; each negative pole is
    // 180° opposite. +I and +aVF carry the vector colours (red a / green b); the rest are dark ink.
    private static readonly (double Deg, string Grad, string Lead, Brush Tint)[] HexaxialTips =
    {
        (0,   "0°",    "+I",   VectorA),
        (30,  "+30°",  "-aVR", AxisMain),
        (60,  "+60°",  "+II",  AxisMain),
        (90,  "+90°",  "+aVF", VectorB),
        (120, "+120°", "+III", AxisMain),
        (150, "+150°", "-aVL", AxisMain),
        (180, "180°",  "-I",   AxisMain),
        (210, "-150°", "+aVR", AxisMain),
        (240, "-120°", "-II",  AxisMain),
        (270, "-90°",  "-aVF", AxisMain),
        (300, "-60°",  "-III", AxisMain),
        (330, "-30°",  "+aVL", AxisMain),
    };

    // A spoke-tip caption placed at a hexaxial angle (0°→right, +90°→down), centred just outside the
    // reference circle: the frontal-plane angle (small, faint grey) stacked over the signed lead name
    // (bold, in its tint). Glyph metrics are approximated to keep the two short lines centred on the tip.
    private static TextBlock TipLabel(double center, double radius, double degrees, string grad, string lead, Brush leadBrush)
    {
        var rad = degrees * Math.PI / 180.0;
        var cx = center + radius * Math.Cos(rad);
        var cy = center + radius * Math.Sin(rad);

        var tb = new TextBlock { TextAlignment = TextAlignment.Center };
        tb.Inlines.Add(new Run { Text = grad, Foreground = Axis, FontSize = 8.5 });
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run { Text = lead, Foreground = leadBrush, FontSize = 10, FontWeight = FontWeights.SemiBold });

        var w = Math.Max(grad.Length, lead.Length) * 5.2;
        Canvas.SetLeft(tb, cx - w / 2);
        Canvas.SetTop(tb, cy - 12);   // ~half the two-line block height
        return tb;
    }

    private const double CaptionFontSize = 9;

    // A curved sector caption: the text laid glyph-by-glyph along an arc at the given radius, centred on
    // the sector's diagonal, so the words "round" with the circle as on the teaching slide. On the bottom
    // half glyphs sit upright reading left→right (rotation = angle − 90, angles running down); on the top
    // half they flip to stay readable (rotation = angle + 90, angles running up). Each glyph advances by
    // its own approximate width (proportional kerning) so narrow letters and spaces don't gap out.
    private static void CurvedCaption(Canvas canvas, double center, double radius, double centerDeg, string text)
    {
        var bottom = Math.Sin(centerDeg * Math.PI / 180.0) > 0;
        var bsign = bottom ? 1.0 : -1.0;                 // maps reading order to sweep direction
        var degPerPx = 180.0 / Math.PI / radius;

        double total = 0;
        var widths = new double[text.Length];
        for (var i = 0; i < text.Length; i++) { widths[i] = GlyphWidth(text[i]) * CaptionFontSize; total += widths[i]; }

        double sPx = 0;   // px walked from the reading-start edge to the current glyph's left
        for (var i = 0; i < text.Length; i++)
        {
            var midPx = sPx + widths[i] / 2.0;           // this glyph's centre, px from the start edge
            var deg = centerDeg + bsign * (total / 2.0 - midPx) * degPerPx;
            sPx += widths[i];

            var rad = deg * Math.PI / 180.0;
            var gx = center + radius * Math.Cos(rad);
            var gy = center + radius * Math.Sin(rad);

            var tb = new TextBlock
            {
                Text = text[i].ToString(),
                Foreground = Axis,
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = bottom ? deg - 90 : deg + 90 },
            };
            Canvas.SetLeft(tb, gx - widths[i] / 2.0);
            Canvas.SetTop(tb, gy - CaptionFontSize * 0.65);
            canvas.Children.Add(tb);
        }
    }

    // Rough proportional glyph width (in em) for kerning the curved captions — enough to keep the spacing
    // even without measuring. Non-Latin glyphs (e.g. Cyrillic captions) fall through to the default.
    private static double GlyphWidth(char ch) => ch switch
    {
        ' ' => 0.32,
        'i' or 'j' or 'l' or 'I' or '.' or ',' or '\'' or '!' or ':' => 0.30,
        'f' or 'r' or 't' => 0.38,
        'm' or 'w' or 'M' or 'W' => 0.84,
        >= 'A' and <= 'Z' => 0.64,
        _ => 0.52,
    };

    // A sector sweep-arrow: a faint arc from startDeg to endDeg at the given radius with an arrowhead at
    // the far end, showing the axis rotating away from the horizontal +I axis as the deviation grows. The
    // arc is sampled as a polyline; the head is two short barbs tangent to the sweep direction at the end.
    private static void SweepArc(Canvas canvas, double center, double radius, double startDeg, double endDeg)
    {
        var arc = new Polyline { Stroke = Axis, StrokeThickness = 1.2 };
        var steps = Math.Max(2, (int)(Math.Abs(endDeg - startDeg) / 4));
        for (var i = 0; i <= steps; i++)
        {
            var rad = (startDeg + (endDeg - startDeg) * i / steps) * Math.PI / 180.0;
            arc.Points.Add(new Windows.Foundation.Point(center + radius * Math.Cos(rad), center + radius * Math.Sin(rad)));
        }
        canvas.Children.Add(arc);

        // Arrowhead at the end, its barbs pointing back along the sweep direction (sign of the angle step).
        var endRad = endDeg * Math.PI / 180.0;
        var tip = new Windows.Foundation.Point(center + radius * Math.Cos(endRad), center + radius * Math.Sin(endRad));
        var sweep = Math.Sign(endDeg - startDeg);
        var tx = -Math.Sin(endRad) * sweep;   // unit tangent in the sweep direction
        var ty = Math.Cos(endRad) * sweep;
        canvas.Children.Add(ArrowBarb(tip, tx, ty, 26));
        canvas.Children.Add(ArrowBarb(tip, tx, ty, -26));
    }

    // The top boundary marker: one straight horizontal double-headed arrow (◄─►) centred at the top,
    // its ends splaying to either side of the -90° divide, connected across the middle as on the concept
    // slide. A single flat line (not two arcs) so it reads as one arrow, not a tent.
    private static void TopBoundaryArrow(Canvas canvas, double centerX, double y, double halfWidth)
    {
        canvas.Children.Add(new Line
        {
            X1 = centerX - halfWidth, Y1 = y,
            X2 = centerX + halfWidth, Y2 = y,
            Stroke = Axis,
            StrokeThickness = 1.2,
        });
        var left = new Windows.Foundation.Point(centerX - halfWidth, y);
        var right = new Windows.Foundation.Point(centerX + halfWidth, y);
        canvas.Children.Add(ArrowBarb(left, -1, 0, 26));   // left head points left
        canvas.Children.Add(ArrowBarb(left, -1, 0, -26));
        canvas.Children.Add(ArrowBarb(right, 1, 0, 26));   // right head points right
        canvas.Children.Add(ArrowBarb(right, 1, 0, -26));
    }

    // One barb of an arrowhead: a short segment from the tip, back along the reversed tangent rotated ±deg.
    private static Line ArrowBarb(Windows.Foundation.Point tip, double tx, double ty, double deg)
    {
        const double len = 6.5;
        var a = deg * Math.PI / 180.0;
        var bx = -(tx * Math.Cos(a) - ty * Math.Sin(a));
        var by = -(tx * Math.Sin(a) + ty * Math.Cos(a));
        return new Line
        {
            X1 = tip.X,
            Y1 = tip.Y,
            X2 = tip.X + bx * len,
            Y2 = tip.Y + by * len,
            Stroke = Axis,
            StrokeThickness = 1.2,
        };
    }
}
