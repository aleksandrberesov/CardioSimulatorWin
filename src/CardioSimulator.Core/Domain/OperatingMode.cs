namespace CardioSimulator.Core.Domain;

/// <summary>
/// The six operating modes. <see cref="TitleResourceKey"/> is a localization
/// key the UI layer resolves against its string resources. <c>Constructor</c>
/// (formerly <c>Editor</c>) still maps to the <c>mode_editor</c> resource
/// key for Android parity.
/// </summary>
public enum OperatingMode
{
    Teaching,
    Testing,
    Examination,
    OSKE,
    Constructor,
    CourseConstructor,
    OskeConstructor,
    TestConstructor,
}

public static class OperatingModes
{
    public static readonly IReadOnlyList<OperatingMode> All = Enum.GetValues<OperatingMode>();

    /// <summary>
    /// The authoring/constructor modes (ECG, course, OSCE, and test constructors). These are hidden
    /// in the limited (student) edition — see <c>CardioSimulator.App.AppEdition.IsLimited</c>.
    /// </summary>
    public static bool IsConstructor(this OperatingMode mode) => mode switch
    {
        OperatingMode.Constructor => true,
        OperatingMode.CourseConstructor => true,
        OperatingMode.OskeConstructor => true,
        OperatingMode.TestConstructor => true,
        _ => false,
    };

    public static string TitleResourceKey(this OperatingMode mode) => mode switch
    {
        OperatingMode.Teaching => "mode_teaching",
        OperatingMode.Testing => "mode_testing",
        OperatingMode.Examination => "mode_examination",
        OperatingMode.OSKE => "mode_oske",
        OperatingMode.Constructor => "mode_editor",
        OperatingMode.CourseConstructor => "mode_course_constructor",
        OperatingMode.OskeConstructor => "mode_oske_constructor",
        OperatingMode.TestConstructor => "mode_test_constructor",
        _ => mode.ToString(),
    };
}

public sealed record OperatingModeModel(OperatingMode Id, string Description = "");
