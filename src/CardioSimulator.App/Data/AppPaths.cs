namespace CardioSimulator.App.Data;

/// <summary>
/// Per-user storage locations. The Android app keeps prefs in a DataStore and the
/// extracted dataset under <c>filesDir/pathologies</c>; on Windows (unpackaged, no
/// package identity for <c>ApplicationData.Current</c>) the equivalent is a folder
/// under <c>%LOCALAPPDATA%</c>.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CardioSimulator");

    /// <summary>Where the picked ZIP is extracted to (Android: filesDir/pathologies).</summary>
    public static string PathologiesDir { get; } = Path.Combine(Root, "pathologies");

    /// <summary>Where the picked Courses ZIP is extracted to (Android: filesDir/courses).</summary>
    public static string CoursesDir { get; } = Path.Combine(Root, "courses");

    /// <summary>OSCE (ОСКЭ) content root: <c>forms/</c> + <c>answers/</c> (seeded on first run).</summary>
    public static string OskeDir { get; } = Path.Combine(Root, "oske");

    /// <summary>Where graded OSCE attempts are saved (one JSON per attempt).</summary>
    public static string OskeResultsDir { get; } = Path.Combine(OskeDir, "results");

    /// <summary>Self-assessment («Тестирование») tests: one JSON per test (seeded on first run).</summary>
    public static string TestsDir { get; } = Path.Combine(Root, "tests");

    /// <summary>Where graded examination attempts are saved (one JSON per attempt).</summary>
    public static string ExamResultsDir { get; } = Path.Combine(TestsDir, "results");

    /// <summary>The standing question bank: one JSON per question (a subfolder of
    /// <see cref="TestsDir"/>, so the test scan ignores it). Also holds <c>themes.json</c>.</summary>
    public static string QuestionBankDir { get; } = Path.Combine(TestsDir, "bank");

    /// <summary>Copied image stimuli for image-based questions (<c>&lt;id&gt;.&lt;ext&gt;</c>).</summary>
    public static string TestImagesDir { get; } = Path.Combine(TestsDir, "images");

    /// <summary>The editable theme catalog for the question bank.</summary>
    public static string TestThemesFile { get; } = Path.Combine(QuestionBankDir, "themes.json");

    /// <summary>User-chosen 3D heart model override (<c>heart.&lt;ext&gt;</c>); overrides the bundled
    /// <c>Assets/Models/heart.*</c> when present.</summary>
    public static string ModelsDir { get; } = Path.Combine(Root, "models");

    public static string PrefsFile { get; } = Path.Combine(Root, "prefs.json");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
