namespace CardioSimulator.App.Data;

/// <summary>
/// Per-user storage locations. The Android app keeps prefs in a DataStore and the
/// extracted dataset under <c>filesDir/pathologies</c>; on Windows (unpackaged, no
/// package identity for <c>ApplicationData.Current</c>) the equivalent is a folder
/// under <c>%LOCALAPPDATA%</c>.
/// </summary>
public static class AppPaths
{
    /// <summary>Per-user data root: <c>%LOCALAPPDATA%\{BuildInfo.DataFolder}</c>. The folder name is
    /// brand-derived (see Directory.Build.props); it was renamed when the app was rebranded and the old
    /// tree is migrated across once on first run — see the static constructor and <see cref="LegacyRoot"/>.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        BuildInfo.DataFolder);

    /// <summary>The pre-rebrand data root. Migrated to <see cref="Root"/> the first time this type is
    /// touched, so a rename doesn't orphan existing prefs, students, exam results and content overlays.</summary>
    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        BuildInfo.LegacyDataFolder);

    /// <summary>
    /// One-time migration of the per-user data folder after a rebrand. Runs on first access to any
    /// AppPaths member (the type initializer), so it always precedes the first path use — including the
    /// crash logger's <c>Directory.CreateDirectory(Root)</c>. If the new folder doesn't exist yet but the
    /// legacy one does, the whole tree is moved across. Both roots live under LocalApplicationData (same
    /// volume), so <see cref="Directory.Move"/> is an atomic rename. Best-effort: never throws, so a
    /// locked or partial legacy folder can't stop the app from starting with a fresh <see cref="Root"/>.
    /// </summary>
    static AppPaths()
    {
        try
        {
            if (!string.Equals(Root, LegacyRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(LegacyRoot) && !Directory.Exists(Root))
            {
                Directory.Move(LegacyRoot, Root);
            }
        }
        catch
        {
            // ignore — EnsureRoot() creates a fresh Root below
        }
    }

    // No pathologies/ or courses/ directory: both datasets are encrypted content packs, read into
    // memory from wherever the pack file lives. Nothing is extracted, so no plaintext content
    // directory exists under Root at all.

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

    /// <summary>User-chosen 3D heart model override (<c>heart.&lt;ext&gt;</c>); overrides the bundled
    /// <c>Assets/Models/heart.*</c> when present.</summary>
    public static string ModelsDir { get; } = Path.Combine(Root, "models");

    /// <summary>
    /// Encrypted writable overlays used by the Full-edition constructor when the dataset is a
    /// protected content pack. Each is a single AES-256-GCM <c>*.pak</c> holding the instructor's
    /// deltas (edits/creations/tombstones) — no plaintext, even for content copied from the bundle.
    /// Absent in file/author mode and in the Limited edition.
    /// </summary>
    public static string OverlayDir { get; } = Path.Combine(Root, "overlay");
    public static string PathologyOverlayPak { get; } = Path.Combine(OverlayDir, "pathologies.pak");
    public static string CourseOverlayPak { get; } = Path.Combine(OverlayDir, "courses.pak");

    /// <summary>
    /// The overlay for a <i>user-picked</i> pathology pack. Keyed by the pack's own content identity
    /// so each pack carries its own deltas: without this, switching packs would replay one pack's edits
    /// and tombstones onto another's ids. <see cref="PathologyOverlayPak"/> stays reserved for the
    /// bundled pack so existing overlays keep resolving.
    /// </summary>
    public static string PathologyOverlayPakFor(string packPath) =>
        Path.Combine(OverlayDir, $"pathologies-{PackKey(packPath)}.pak");

    /// <summary>The overlay for a user-picked course pack. Same per-pack keying as
    /// <see cref="PathologyOverlayPakFor"/>.</summary>
    public static string CourseOverlayPakFor(string packPath) =>
        Path.Combine(OverlayDir, $"courses-{PackKey(packPath)}.pak");

    /// <summary>
    /// A short, stable, filename-safe key for a pack's overlay. Derived from the pack's own random
    /// identity (its salt/nonce) rather than its file path, so re-exporting a pack over a path already
    /// in use draws a <i>fresh</i> overlay instead of inheriting the previous pack's edits and
    /// tombstones — which would otherwise hide the new pack's content, leaving it "loading empty, only
    /// the structure". Re-reading one unchanged pack still yields the same key, so its edits persist.
    /// Falls back to a digest of the path for a file that cannot be read as a pack (e.g. a stale pick
    /// on a disconnected drive), preserving the old behaviour in that case.
    /// </summary>
    private static string PackKey(string packPath)
    {
        try
        {
            var info = new FileInfo(packPath);
            if (info.Exists)
            {
                if (CardioSimulator.Core.Data.ContentCrypto.TryReadPackIdentity(packPath, out var identity))
                {
                    var combined = identity
                        .Concat(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks))
                        .Concat(BitConverter.GetBytes(info.Length))
                        .ToArray();
                    return Digest(combined);
                }

                var pathBytes = System.Text.Encoding.UTF8.GetBytes(packPath.Replace('/', '\\').ToLowerInvariant());
                var fallbackCombined = pathBytes
                    .Concat(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks))
                    .Concat(BitConverter.GetBytes(info.Length))
                    .ToArray();
                return Digest(fallbackCombined);
            }
        }
        catch
        {
            /* fall through to PathKey */
        }

        return PathKey(packPath);
    }

    /// <summary>A short, stable, filename-safe digest of a pack path (case- and separator-insensitive,
    /// matching Windows path semantics). Fallback key when the pack's own identity is unreadable.</summary>
    private static string PathKey(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return Digest(System.Text.Encoding.UTF8.GetBytes(normalized));
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8).ToLowerInvariant();

    public static string PrefsFile { get; } = Path.Combine(Root, "prefs.json");

    /// <summary>The instructor's student roster (one JSON array), managed from the Full-edition
    /// Students registration screen and offered as an exam start pick-list. See
    /// <c>CardioSimulator.Core.Data.StudentStore</c>.</summary>
    public static string StudentsFile { get; } = Path.Combine(Root, "students.json");

    /// <summary>Persisted state for the Learning Quality («Качество обучения») dashboard: per-section /
    /// per-subtopic progress plus the set of completed adaptive-plan tasks (one JSON file). Mirrors
    /// the prototype's <c>localStorage</c> so progress survives restarts.</summary>
    public static string LearningScaleFile { get; } = Path.Combine(Root, "learning-scale.json");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
