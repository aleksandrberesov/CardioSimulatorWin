using CardioSimulator.Core.Domain;
using Windows.UI;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// A paper-grid scheme: the background, the small/large grid line colors, and the
/// <see cref="Trace"/> color used for the waveform, calibration pulse, and lead label
/// (so each lead's title reads as the same color as its line).
/// </summary>
public readonly record struct GridPalette(Color Background, Color SmallLine, Color LargeLine, Color Trace);

/// <summary>
/// Paper-grid and trace colors. The default look is a teal trace on cream paper
/// (the <see cref="GridScheme.Yellow"/> palette), with a blue-gray alternate and a
/// pink palette that pairs a rose grid with black graphics.
/// </summary>
public static class EcgColors
{
    // Teal trace (matches AppTheme EcgTrace token) used by the cream and blue-gray paper.
    private static readonly Color Teal = Color.FromArgb(0xFF, 0x2C, 0x6E, 0x8E);
    // Near-black graphics used by the pink paper.
    private static readonly Color Black = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);

    /// <summary>
    /// The "bedside monitor" sheet: a bright green trace on a black background with no grid
    /// (the classic scope look). Selected via <see cref="Core.Domain.MonitorModeModel.BlankSheet"/>.
    /// The grid-line colors are unused (no grid is drawn) but mirror the background.
    /// </summary>
    public static readonly GridPalette Bedside = new(
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        Color.FromArgb(0xFF, 0x00, 0xE0, 0x4A));

    public static GridPalette Palette(GridScheme scheme) => scheme switch
    {
        GridScheme.BlueGray => new GridPalette(
            Color.FromArgb(0xFF, 0xF0, 0xF4, 0xF7),
            Color.FromArgb(0xFF, 0xDD, 0xE4, 0xE9),
            Color.FromArgb(0xFF, 0xBC, 0xC6, 0xCF),
            Teal),
        // Pink ECG paper with rose grid lines and black graphics (mirrors EcgSvgRenderer's figures).
        GridScheme.Pink => new GridPalette(
            Color.FromArgb(0xFF, 0xFF, 0xF5, 0xF5),
            Color.FromArgb(0xFF, 0xFD, 0xE4, 0xE4),
            Color.FromArgb(0xFF, 0xF9, 0xBD, 0xBD),
            Black),
        // Cream ECG paper with khaki grid lines (the default "yellow" look).
        _ => new GridPalette(
            Color.FromArgb(0xFF, 0xFC, 0xFC, 0xEC),
            Color.FromArgb(0xFF, 0xE6, 0xE4, 0xCE),
            Color.FromArgb(0xFF, 0xD2, 0xCE, 0xA6),
            Teal),
    };

    /// <summary>
    /// Effective palette for the monitor, switching to the <see cref="Bedside"/> sheet
    /// (green on black, no grid) when the blank-sheet ("bedside monitor") mode is on.
    /// </summary>
    public static GridPalette Palette(GridScheme scheme, bool blankSheet) =>
        blankSheet ? Bedside : Palette(scheme);
}
