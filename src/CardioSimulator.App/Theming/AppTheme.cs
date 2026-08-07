using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Theming;

/// <summary>
/// Code-behind access to the app design tokens defined in <c>App.xaml</c>. Resolves the same
/// resource keys (single source of truth) so C#-built controls share one palette with XAML.
/// Supports dynamic theme switching (Light / Dark) matching the template pattern.
/// </summary>
public static class AppTheme
{
    /// <summary>Raised after the theme changes, so view models/views can re-pull theme-dependent brushes.</summary>
    public static event Action? Changed;

    /// <summary>The active theme. Defaults to Light until restored or changed.</summary>
    public static ElementTheme Current { get; private set; } = ElementTheme.Light;

    public static bool IsDark => Current == ElementTheme.Dark;

    /// <summary>Switches theme, applies to window root, clears brush cache, and notifies listeners.</summary>
    public static void Set(bool dark)
    {
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (theme == Current && Cache.Count > 0) return;

        Current = theme;
        Cache.Clear();
        ApplyToRoot();
        Changed?.Invoke();
    }

    /// <summary>
    /// Pushes the theme onto the window root.
    /// </summary>
    public static void ApplyToRoot()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Current;
        }
    }

    // ── NeoCryBaby Standard Tokens ──────────────────────────────────────────
    public static SolidColorBrush AppPageBackground => Brush("AppPageBackgroundBrush", AppPageBackgroundColor);
    public static SolidColorBrush AppCardBackground => Brush("AppCardBackgroundBrush", AppCardBackgroundColor);
    public static SolidColorBrush AppCardBorder => Brush("AppCardBorderBrush", AppCardBorderColor);
    public static SolidColorBrush AppSubtleFill => Brush("AppSubtleFillBrush", AppSubtleFillColor);
    public static SolidColorBrush AppInputBackground => Brush("AppInputBackgroundBrush", AppInputBackgroundColor);
    public static SolidColorBrush AppTextPrimary => Brush("AppTextPrimaryBrush", AppTextPrimaryColor);
    public static SolidColorBrush AppTextSecondary => Brush("AppTextSecondaryBrush", AppTextSecondaryColor);
    public static SolidColorBrush AppAccentSoftBackground => Brush("AppAccentSoftBackgroundBrush", AppAccentSoftBackgroundColor);
    public static SolidColorBrush AppAccentSoftBorder => Brush("AppAccentSoftBorderBrush", AppAccentSoftBorderColor);
    public static SolidColorBrush AppSuccessSoftBackground => Brush("AppSuccessSoftBackgroundBrush", AppSuccessSoftBackgroundColor);

    // ── Chrome Brushes (Theme-Invariant) ────────────────────────────────────
    public static SolidColorBrush ChromeBackground => Brush("ChromeBackgroundBrush", Rgb(0x1C, 0x1C, 0x1E));
    public static SolidColorBrush ChromeSurface => Brush("ChromeSurfaceBrush", Rgb(0x2C, 0x2C, 0x2E));
    public static SolidColorBrush ChromeBorder => Brush("ChromeBorderBrush", Rgb(0x38, 0x38, 0x3A));
    public static SolidColorBrush ChromeTextPrimary => Brush("ChromeTextPrimaryBrush", Rgb(0xFF, 0xFF, 0xFF));
    public static SolidColorBrush ChromeTextSecondary => Brush("ChromeTextSecondaryBrush", Rgb(0x8E, 0x8E, 0x93));

    // ── Legacy Aliases ──────────────────────────────────────────────────────
    public static SolidColorBrush Accent => Brush("AccentBrush", AccentColor);
    public static SolidColorBrush AccentTint => Brush("AccentTintBrush", AccentTintColor);
    public static SolidColorBrush OnAccent => Brush("OnAccentBrush", OnAccentColor);
    public static SolidColorBrush PageBackground => AppPageBackground;
    public static SolidColorBrush PanelBackground => AppCardBackground;
    public static SolidColorBrush ControlFill => AppSubtleFill;
    public static SolidColorBrush ControlBorder => AppCardBorder;
    public static SolidColorBrush Hairline => AppCardBorder;
    public static SolidColorBrush HoverFill => Brush("HoverFillBrush", HoverFillColor);
    public static SolidColorBrush TextPrimary => AppTextPrimary;
    public static SolidColorBrush TextSecondary => AppTextSecondary;
    public static SolidColorBrush Positive => Brush("PositiveBrush", PositiveColor);
    public static SolidColorBrush Negative => Brush("NegativeBrush", NegativeColor);

    // ── Colors ──────────────────────────────────────────────────────────────
    public static Color AccentColor => Rgb(0x33, 0xA0, 0x6A);
    public static Color AccentTintColor => IsDark ? Rgb(0x2A, 0x2A, 0x3D) : Rgb(0xEE, 0xF0, 0xFF);
    public static Color OnAccentColor => Rgb(0xFF, 0xFF, 0xFF);
    public static Color AppPageBackgroundColor => IsDark ? Rgb(0x00, 0x00, 0x00) : Rgb(0xF2, 0xF2, 0xF7);
    public static Color AppCardBackgroundColor => IsDark ? Rgb(0x1C, 0x1C, 0x1E) : Rgb(0xFF, 0xFF, 0xFF);
    public static Color AppCardBorderColor => IsDark ? Rgb(0x38, 0x38, 0x3A) : Rgb(0xD1, 0xD1, 0xD6);
    public static Color AppSubtleFillColor => IsDark ? Rgb(0x2C, 0x2C, 0x2E) : Rgb(0xE5, 0xE5, 0xEA);
    public static Color AppInputBackgroundColor => IsDark ? Rgb(0x2C, 0x2C, 0x2E) : Rgb(0xF9, 0xF9, 0xFB);
    public static Color AppTextPrimaryColor => IsDark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1C, 0x1C, 0x1E);
    public static Color AppTextSecondaryColor => Rgb(0x8E, 0x8E, 0x93);
    public static Color AppAccentSoftBackgroundColor => IsDark ? Rgb(0x2A, 0x2A, 0x3D) : Rgb(0xEE, 0xF0, 0xFF);
    public static Color AppAccentSoftBorderColor => IsDark ? Rgb(0x4A, 0x4A, 0x6A) : Rgb(0xC7, 0xCC, 0xF5);
    public static Color AppSuccessSoftBackgroundColor => IsDark ? Rgb(0x14, 0x35, 0x1A) : Rgb(0xE5, 0xF9, 0xE7);

    public static Color PageBackgroundColor => AppPageBackgroundColor;
    public static Color PanelBackgroundColor => AppCardBackgroundColor;
    public static Color ControlFillColor => AppSubtleFillColor;
    public static Color ControlBorderColor => AppCardBorderColor;
    public static Color HairlineColor => AppCardBorderColor;
    public static Color HoverFillColor => IsDark ? Argb(0x28, 0xFF, 0xFF, 0xFF) : Argb(0x14, 0x80, 0x80, 0x80);
    public static Color TextPrimaryColor => AppTextPrimaryColor;
    public static Color TextSecondaryColor => AppTextSecondaryColor;
    public static Color PositiveColor => Rgb(0x2E, 0x9E, 0x5B);
    public static Color NegativeColor => Rgb(0xCC, 0x3A, 0x3A);

    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        SolidColorBrush brush = new SolidColorBrush(fallback);
        Cache[key] = brush;
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Argb(0xFF, r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
}
