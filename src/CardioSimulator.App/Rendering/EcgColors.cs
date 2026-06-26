using CardioSimulator.Core.Domain;
using Windows.UI;

namespace CardioSimulator.App.Rendering;

public readonly record struct GridPalette(Color Background, Color SmallLine, Color LargeLine);

/// <summary>
/// Paper-grid and trace colors. The new UI pattern uses a teal trace on cream paper
/// (the default <see cref="GridScheme.Pink"/> palette), with a blue-gray alternate.
/// </summary>
public static class EcgColors
{
    // Teal trace + dark-slate label (matches AppTheme EcgTrace / TextPrimary tokens).
    public static readonly Color Trace = Color.FromArgb(0xFF, 0x2C, 0x6E, 0x8E);
    public static readonly Color Label = Color.FromArgb(0xFF, 0x1B, 0x24, 0x30);

    public static GridPalette Palette(GridScheme scheme) => scheme switch
    {
        GridScheme.BlueGray => new GridPalette(
            Color.FromArgb(0xFF, 0xF0, 0xF4, 0xF7),
            Color.FromArgb(0xFF, 0xDD, 0xE4, 0xE9),
            Color.FromArgb(0xFF, 0xBC, 0xC6, 0xCF)),
        // Cream ECG paper with khaki grid lines (the new default look).
        _ => new GridPalette(
            Color.FromArgb(0xFF, 0xFC, 0xFC, 0xEC),
            Color.FromArgb(0xFF, 0xE6, 0xE4, 0xCE),
            Color.FromArgb(0xFF, 0xD2, 0xCE, 0xA6)),
    };
}
