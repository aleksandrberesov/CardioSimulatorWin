namespace CardioSimulator.Core.Domain;

/// <summary>
/// The operating modes. <see cref="TitleResourceKey"/> is a localization
/// key the UI layer resolves against its string resources. <c>Constructor</c>
/// (formerly <c>Editor</c>) still maps to the <c>mode_editor</c> resource
/// key for Android parity. <see cref="LearningScale"/> is a student-facing
/// progress dashboard («Шкала обучения»); it is declared last so the existing
/// modes (and their number-key shortcuts) keep their positions, and it is not
/// swept up by <see cref="OperatingModes.IsConstructor"/> (it stays visible in
/// the Limited student edition). <see cref="Students"/> is an instructor-only
/// roster screen for registering students; like the constructors it is Full-edition
/// only (see <see cref="OperatingModes.IsFullEditionOnly"/>) and is appended last so
/// the existing modes keep their positions and number-key shortcuts. The treatment/resuscitation
/// simulation («Лечение») is NOT a mode — it is a panel inside Teaching, toggled from a bottom-bar button.
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
    LearningScale,
    Students,
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

    /// <summary>
    /// Modes present only in the Full (instructor) edition and hidden in the Limited (student) build:
    /// every authoring/constructor mode plus the student-registration roster. This is the single
    /// predicate the mode-builder filters on — see <c>CardioSimulator.App.AppEdition.IsLimited</c>.
    /// </summary>
    public static bool IsFullEditionOnly(this OperatingMode mode) =>
        mode.IsConstructor() || mode == OperatingMode.Students;

    public static string TitleResourceKey(this OperatingMode mode) => mode switch
    {
        OperatingMode.Teaching => "mode_teaching",
        OperatingMode.Testing => "mode_testing",
        OperatingMode.Examination => "mode_examination",
        OperatingMode.OSKE => "mode_oske",
        OperatingMode.LearningScale => "mode_learning_scale",
        OperatingMode.Students => "mode_students",
        OperatingMode.Constructor => "mode_editor",
        OperatingMode.CourseConstructor => "mode_course_constructor",
        OperatingMode.OskeConstructor => "mode_oske_constructor",
        OperatingMode.TestConstructor => "mode_test_constructor",
        _ => mode.ToString(),
    };
}

public sealed record OperatingModeModel(OperatingMode Id, string Description = "");
