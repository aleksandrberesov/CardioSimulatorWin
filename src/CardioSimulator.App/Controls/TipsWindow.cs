using CardioSimulator.App.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The kinds of annotation overlay the "Подсказки" (Tips) window can place on the monitor trace.
/// Mirrors the customer's "Типы подсказок на ЭКГ" list: an arrow with a caption, a whole-lead
/// highlight, a free area on the graph, a slice of one ECG segment, vertical/horizontal guide
/// lines, and a free-standing label.
/// </summary>
public enum TipOverlayKind
{
    Arrow,
    LeadArea,
    GraphArea,
    EcgPart,
    VerticalLines,
    HorizontalLines,
    Label,
}

/// <summary>
/// "Подсказки" (Tips) window: a semi-transparent panel docked to the right edge of the monitor,
/// overlaying the live trace — the same translucent-blue <see cref="Popup"/> treatment as
/// <see cref="EosWindow"/> so it composites above the native Win2D surface and the ECG shows through.
/// Where EOS reads the trace, Tips *writes* to it: it offers a palette of annotation-overlay kinds
/// (arrow, lead/graph/segment highlight, guide lines, label) that an instructor places at key points,
/// and previews the resulting "Видим:" tip list that pops up on the monitor. Toggled by the panel's
/// Tips button. Currently a scaffold — picking a kind highlights the palette but the placement
/// gesture and the rendered overlays land in a later increment.
/// </summary>
public static class TipsWindow
{
    private const double PanelWidth = 300;

    private static readonly SolidColorBrush PanelFill =
        new(new Windows.UI.Color { A = 0xCC, R = 0x5B, G = 0x9B, B = 0xD5 });
    private static readonly SolidColorBrush White = new(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush ChipFill = new(new Windows.UI.Color { A = 0x26, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush ChipSelectedFill = new(new Windows.UI.Color { A = 0x59, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush ChipBorder = new(new Windows.UI.Color { A = 0x80, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush PreviewBg = new(new Windows.UI.Color { A = 0xF2, R = 255, G = 255, B = 255 });
    private static readonly SolidColorBrush PreviewInk = new(new Windows.UI.Color { A = 255, R = 0x1E, G = 0x5F, B = 0xA5 });
    private static readonly SolidColorBrush PreviewMuted = new(new Windows.UI.Color { A = 255, R = 0x99, G = 0x99, B = 0x99 });
    private static readonly SolidColorBrush Transparent = new(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });

    private static Popup? _popup;
    private static TipOverlayKind? _selectedKind;

    // Built once per Open; lets a chip click restyle its siblings without rebuilding the panel.
    private static readonly List<(TipOverlayKind Kind, Border Chip)> _chips = new();

    /// <summary>Opens the Tips palette on the right of the monitor, or closes it if already open.</summary>
    public static void Toggle(XamlRoot xamlRoot)
    {
        if (_popup is { IsOpen: true })
        {
            Close();
            return;
        }
        Open(xamlRoot);
    }

    /// <summary>Closes the palette if open (e.g. when leaving the monitor).</summary>
    public static void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
        _chips.Clear();
    }

    private static void Open(XamlRoot xamlRoot)
    {
        var size = xamlRoot.Size;
        const double topMargin = 72;    // clears the top mode bar
        const double bottomMargin = 72; // clears the bottom control panel
        const double rightMargin = 16;
        var height = Math.Max(240, size.Height - topMargin - bottomMargin);

        _chips.Clear();
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
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Title(AppStrings.MonitorTipsWindowTitle));
        content.Children.Add(SectionHeader(AppStrings.MonitorTipsTypesHeader));

        content.Children.Add(TypeChip(1, TipOverlayKind.Arrow, AppStrings.MonitorTipsTypeArrow));
        content.Children.Add(TypeChip(2, TipOverlayKind.LeadArea, AppStrings.MonitorTipsTypeLeadArea));
        content.Children.Add(TypeChip(3, TipOverlayKind.GraphArea, AppStrings.MonitorTipsTypeGraphArea));
        content.Children.Add(TypeChip(4, TipOverlayKind.EcgPart, AppStrings.MonitorTipsTypeEcgPart));
        content.Children.Add(TypeChip(5, TipOverlayKind.VerticalLines, AppStrings.MonitorTipsTypeVerticalLines));
        content.Children.Add(TypeChip(6, TipOverlayKind.HorizontalLines, AppStrings.MonitorTipsTypeHorizontalLines));
        content.Children.Add(TypeChip(7, TipOverlayKind.Label, AppStrings.MonitorTipsTypeLabel));

        content.Children.Add(Preview());
        content.Children.Add(Note(AppStrings.MonitorTipsNote));

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
            Background = Transparent,
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
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A selectable overlay-kind chip: a numbered badge, the type's glyph, and its label.
    /// Clicking selects it (one at a time) — the scaffold for "now place this overlay on the trace".</summary>
    private static Border TypeChip(int number, TipOverlayKind kind, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = $"{number}.",
            Foreground = White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18,
        });
        row.Children.Add(new Border
        {
            Width = 26,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Child = KindIcon(kind),
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var chip = new Border
        {
            Background = _selectedKind == kind ? ChipSelectedFill : ChipFill,
            BorderBrush = ChipBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Child = row,
        };
        chip.Tapped += (_, _) => Select(kind);
        _chips.Add((kind, chip));
        return chip;
    }

    private static void Select(TipOverlayKind kind)
    {
        _selectedKind = kind;
        foreach (var (k, chip) in _chips)
            chip.Background = k == kind ? ChipSelectedFill : ChipFill;
        // Placing the overlay on the trace lands in a later increment.
    }

    /// <summary>A tiny white-on-blue pictogram hinting at what each overlay kind looks like on the trace.</summary>
    private static UIElement KindIcon(TipOverlayKind kind)
    {
        var canvas = new Canvas { Width = 26, Height = 20 };
        switch (kind)
        {
            case TipOverlayKind.Arrow:
                canvas.Children.Add(Stroke(2, 14, 20, 4));
                canvas.Children.Add(Stroke(20, 4, 14, 5));
                canvas.Children.Add(Stroke(20, 4, 19, 10));
                break;
            case TipOverlayKind.LeadArea:
                canvas.Children.Add(Box(1, 2, 24, 16, 0x4D));
                break;
            case TipOverlayKind.GraphArea:
                canvas.Children.Add(Box(7, 4, 12, 12, 0x4D));
                break;
            case TipOverlayKind.EcgPart:
                canvas.Children.Add(Stroke(1, 16, 7, 16));
                canvas.Children.Add(Stroke(7, 16, 10, 4));
                canvas.Children.Add(Stroke(10, 4, 14, 16));
                canvas.Children.Add(Stroke(14, 16, 25, 16));
                break;
            case TipOverlayKind.VerticalLines:
                canvas.Children.Add(Stroke(8, 2, 8, 18));
                canvas.Children.Add(Stroke(18, 2, 18, 18));
                break;
            case TipOverlayKind.HorizontalLines:
                canvas.Children.Add(Stroke(2, 6, 24, 6));
                canvas.Children.Add(Stroke(2, 14, 24, 14));
                break;
            case TipOverlayKind.Label:
                canvas.Children.Add(Box(2, 3, 22, 14, 0x4D));
                canvas.Children.Add(Stroke(6, 8, 20, 8));
                canvas.Children.Add(Stroke(6, 12, 16, 12));
                break;
        }
        return canvas;
    }

    private static Line Stroke(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = White,
        StrokeThickness = 1.6,
    };

    private static Rectangle Box(double x, double y, double w, double h, byte fillAlpha) => new()
    {
        Width = w,
        Height = h,
        Stroke = White,
        StrokeThickness = 1.2,
        Fill = new SolidColorBrush(new Windows.UI.Color { A = fillAlpha, R = 255, G = 255, B = 255 }),
        Margin = new Thickness(x, y, 0, 0),
    };

    /// <summary>The "Видим:" preview — a near-opaque white card standing in for the tip pop-up that
    /// appears on the monitor, with numbered placeholder lines for the points the instructor adds.</summary>
    private static UIElement Preview()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.MonitorTipsPreviewHeader,
            Foreground = PreviewInk,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        for (var i = 1; i <= 4; i++)
            stack.Children.Add(new TextBlock
            {
                Text = $"{i}.  ……",
                Foreground = PreviewMuted,
                FontSize = 14,
            });

        return new Border
        {
            Background = PreviewBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = stack,
        };
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = White,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
    };
}
