namespace CardioSimulator.App;

/// <summary>
/// The build edition of the app. <see cref="IsLimited"/> is true only in the "Limited" build
/// configuration (which defines the <c>LIMITED</c> compile symbol — see
/// <c>CardioSimulator.App.csproj</c> and <c>Directory.Build.props</c>).
///
/// The limited edition is the locked-down build handed to end users (students): the four
/// authoring/constructor operating modes are hidden from the mode picker and shortcuts, and the
/// ECG-data / course-data import &amp; export controls are removed from the Settings dialog. Because
/// the flag is a compile-time constant, the C# compiler folds the <c>if (AppEdition.IsLimited)</c>
/// branches at build time and the full-edition entry points are genuinely absent from that binary.
/// </summary>
public static class AppEdition
{
#if LIMITED
    public const bool IsLimited = true;
#else
    public const bool IsLimited = false;
#endif

    /// <summary>Convenience inverse of <see cref="IsLimited"/> for readability at call sites.</summary>
    public const bool IsFull = !IsLimited;
}
