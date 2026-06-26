using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Theming;

/// <summary>
/// Code-behind access to the app design tokens defined in <c>App.xaml</c>. Resolves the same
/// resource keys (single source of truth) so C#-built controls share one palette with XAML.
/// Brushes are cached app singletons — safe to assign to many elements. Use the <c>*Color</c>
/// accessors where a <see cref="Color"/> (not a brush) is needed, e.g. Win2D drawing.
/// </summary>
public static class AppTheme
{
    // ── Brushes ─────────────────────────────────────────────────────────────
    public static SolidColorBrush Accent => Brush("AccentBrush", AccentColor);
    public static SolidColorBrush AccentTint => Brush("AccentTintBrush", AccentTintColor);
    public static SolidColorBrush OnAccent => Brush("OnAccentBrush", OnAccentColor);
    public static SolidColorBrush PageBackground => Brush("PageBackgroundBrush", PageBackgroundColor);
    public static SolidColorBrush PanelBackground => Brush("PanelBackgroundBrush", PanelBackgroundColor);
    public static SolidColorBrush ControlFill => Brush("ControlFillBrush", ControlFillColor);
    public static SolidColorBrush ControlBorder => Brush("ControlBorderBrush", ControlBorderColor);
    public static SolidColorBrush Hairline => Brush("HairlineBrush", HairlineColor);
    public static SolidColorBrush HoverFill => Brush("HoverFillBrush", HoverFillColor);
    public static SolidColorBrush TextPrimary => Brush("TextPrimaryBrush", TextPrimaryColor);
    public static SolidColorBrush TextSecondary => Brush("TextSecondaryBrush", TextSecondaryColor);

    // ── Colors (fallbacks mirror App.xaml; also used directly by Win2D) ──────
    public static Color AccentColor => Rgb(0x33, 0xA0, 0x6A);
    public static Color AccentTintColor => Rgb(0xDC, 0xF1, 0xE6);
    public static Color OnAccentColor => Rgb(0xFF, 0xFF, 0xFF);
    public static Color PageBackgroundColor => Rgb(0xE8, 0xEA, 0xF4);
    public static Color PanelBackgroundColor => Rgb(0xFF, 0xFF, 0xFF);
    public static Color ControlFillColor => Rgb(0xEF, 0xF1, 0xF7);
    public static Color ControlBorderColor => Rgb(0xE0, 0xE4, 0xEC);
    public static Color HairlineColor => Rgb(0xE2, 0xE5, 0xEE);
    public static Color HoverFillColor => Argb(0x14, 0x80, 0x80, 0x80);
    public static Color TextPrimaryColor => Rgb(0x1B, 0x24, 0x30);
    public static Color TextSecondaryColor => Rgb(0x5A, 0x6B, 0x82);

    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        SolidColorBrush brush;
        if (Application.Current?.Resources.TryGetValue(key, out var res) == true && res is SolidColorBrush b)
            brush = b;
        else
            brush = new SolidColorBrush(fallback);
        Cache[key] = brush;
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Argb(0xFF, r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
}
