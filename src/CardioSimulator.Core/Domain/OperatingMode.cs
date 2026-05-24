namespace CardioSimulator.Core.Domain;

/// <summary>
/// The five operating modes. <see cref="TitleResourceKey"/> is a localization
/// key the UI layer resolves against its string resources.
/// </summary>
public enum OperatingMode
{
    Teaching,
    Testing,
    Examination,
    OSKE,
    Editor,
}

public static class OperatingModes
{
    public static readonly IReadOnlyList<OperatingMode> All = Enum.GetValues<OperatingMode>();

    public static string TitleResourceKey(this OperatingMode mode) => mode switch
    {
        OperatingMode.Teaching => "mode_teaching",
        OperatingMode.Testing => "mode_testing",
        OperatingMode.Examination => "mode_examination",
        OperatingMode.OSKE => "mode_oske",
        OperatingMode.Editor => "mode_editor",
        _ => mode.ToString(),
    };
}

public sealed record OperatingModeModel(OperatingMode Id, string Description = "");
