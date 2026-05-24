using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Localization;

/// <summary>
/// English UI strings, mirroring the Android <c>res/values/strings.xml</c> keys.
/// Temporary single-language home until the localization milestone introduces a
/// ResourceLoader/.resw set (RU/ZH/ES) and runtime language switching.
/// </summary>
public static class AppStrings
{
    public const string DataSourceTitle = "ECG Data";
    public const string DataSourceDescription = "Select a ZIP archive on your device containing the ECG data.";
    public const string DataSourcePickFolder = "Select ZIP archive";
    public const string DataSourceChangeFolder = "Change ZIP archive";
    public const string DataSourceLoading = "Loading data…";
    public const string DataSourceContinue = "Continue";
    public const string DataSourceRetry = "Pick another ZIP";
    public const string DataSourceShowDetails = "Show Details";
    public const string DataSourceClose = "Close";
    public const string DataSourceErrorBadManifest = "The ZIP archive contains an invalid or missing manifest file.";
    public const string DataSourceErrorUnreadable = "The selected ZIP archive is no longer accessible.";
    public const string DataSourceErrorEmpty = "No ECG files were found in the selected ZIP archive.";

    public const string RhythmSearchPlaceholder = "Rhythm…";

    public static string DataSourceLoadedFormat(int count) => $"Loaded {count} pathologies";

    public static string DataSourcePathologiesTitle(int count) => $"Loaded Pathologies ({count})";

    /// <summary>Display name for an operating mode (English; localized in the i18n milestone).</summary>
    public static string ModeName(OperatingMode mode) => mode switch
    {
        OperatingMode.Teaching => "Teaching",
        OperatingMode.Testing => "Testing",
        OperatingMode.Examination => "Examination",
        OperatingMode.OSKE => "OSKE",
        OperatingMode.Editor => "Editor",
        _ => mode.ToString(),
    };
}
