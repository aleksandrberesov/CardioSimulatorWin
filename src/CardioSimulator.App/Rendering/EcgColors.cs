using CardioSimulator.Core.Domain;
using Windows.UI;

namespace CardioSimulator.App.Rendering;

public readonly record struct GridPalette(Color Background, Color SmallLine, Color LargeLine);

/// <summary>Paper-grid and trace colors, matching the Android Compose schemes.</summary>
public static class EcgColors
{
    public static readonly Color Trace = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    public static readonly Color Label = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

    public static GridPalette Palette(GridScheme scheme) => scheme switch
    {
        GridScheme.BlueGray => new GridPalette(
            Color.FromArgb(0xFF, 0xF0, 0xF4, 0xF7),
            Color.FromArgb(0xFF, 0xDD, 0xE4, 0xE9),
            Color.FromArgb(0xFF, 0xBC, 0xC6, 0xCF)),
        _ => new GridPalette(
            Color.FromArgb(0xFF, 0xFF, 0xF5, 0xF5),
            Color.FromArgb(0xFF, 0xFD, 0xE4, 0xE4),
            Color.FromArgb(0xFF, 0xF9, 0xBD, 0xBD)),
    };
}
