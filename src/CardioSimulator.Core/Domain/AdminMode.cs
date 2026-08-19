namespace CardioSimulator.Core.Domain;

/// <summary>
/// The runtime role layer inside the <b>Full</b> edition (it has no effect in the Limited student
/// build, whose Full-only screens are already stripped at compile time — see
/// <c>CardioSimulator.App.AppEdition</c>). An instructor drops the machine into <see cref="User"/>
/// mode after configuring which screens/blocks students may see; <see cref="Admin"/> mode is
/// PIN-guarded and exposes that configuration. Persisted per-machine.
/// </summary>
public enum AppRole
{
    User,
    Admin,
}

/// <summary>
/// The in-screen blocks an admin can hide from users (finer-grained than whole
/// <see cref="OperatingMode"/> screens). This is the extensible registry: to make a new block
/// hideable, add a value here, gate its construction on
/// <c>AppViewModel.IsBlockVisible(AppBlock.X)</c>, and give it a checklist label in the
/// Administrator settings section. The initial set covers the Settings dialog's data / network /
/// model sections — the ones an instructor is most likely to lock down on a shared machine.
/// </summary>
public enum AppBlock
{
    /// <summary>Settings → ECG data (change / reset / export the ECG dataset).</summary>
    SettingsEcgData,

    /// <summary>Settings → course data (change / reset / export the course pack).</summary>
    SettingsCourseData,

    /// <summary>Settings → TCP monitor connection (target IP/port + connect toggle).</summary>
    SettingsTcp,

    /// <summary>Settings → 3D heart model (load a custom model / reset to bundled).</summary>
    Settings3DModel,
}

public static class AppBlocks
{
    public static readonly IReadOnlyList<AppBlock> All = Enum.GetValues<AppBlock>();
}

/// <summary>
/// Pure helpers for the runtime screen-visibility filter, kept in Core so they are unit-testable
/// without the App project. See <c>AppViewModel.VisibleOperatingModes</c>.
/// </summary>
public static class ModeVisibility
{
    /// <summary>
    /// Whether an admin may hide this mode. <see cref="OperatingMode.Teaching"/> is the app's home
    /// screen and the guaranteed safe landing when a selected mode is hidden, so it is never
    /// hideable — it is always retained by <see cref="Visible"/>.
    /// </summary>
    public static bool IsHideable(this OperatingMode mode) => mode != OperatingMode.Teaching;

    /// <summary>
    /// The modes visible for a given <paramref name="role"/>: the full <paramref name="all"/> list in
    /// <see cref="AppRole.Admin"/> (the admin configures everything, so sees everything), else the
    /// full list minus the <paramref name="hidden"/> set. Non-hideable modes (Teaching) are always
    /// retained regardless of the hidden set.
    /// </summary>
    public static IReadOnlyList<OperatingModeModel> Visible(
        IReadOnlyList<OperatingModeModel> all,
        IReadOnlySet<OperatingMode> hidden,
        AppRole role)
    {
        if (role == AppRole.Admin) return all;
        return all.Where(m => !m.Id.IsHideable() || !hidden.Contains(m.Id)).ToList();
    }
}
