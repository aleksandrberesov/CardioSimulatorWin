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

    public static string PrefsFile { get; } = Path.Combine(Root, "prefs.json");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
