using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CardioSimulator.App.Data;

namespace CardioSimulator.App;

/// <summary>Result of a single <see cref="DemoGuard.Evaluate"/> call.</summary>
/// <param name="IsDemo">True when this is a time-limited demo build (<see cref="BuildInfo.DemoTrialDays"/> &gt; 0).</param>
/// <param name="IsExpired">True when the demo window has passed (or clock rollback was detected). Always false for a non-demo build.</param>
/// <param name="DaysRemaining">Whole days left in the demo window; 0 on the final day, negative once expired.</param>
/// <param name="BuildDate">The UTC date the binary was built (from <see cref="BuildInfo.BuildDate"/>).</param>
/// <param name="ExpiryDate">Last day the demo is usable: <see cref="BuildDate"/> + <see cref="TrialDays"/>.</param>
/// <param name="TrialDays">The configured demo length in days.</param>
public readonly record struct DemoStatus(
    bool IsDemo,
    bool IsExpired,
    int DaysRemaining,
    DateOnly BuildDate,
    DateOnly ExpiryDate,
    int TrialDays);

/// <summary>
/// Time-limited "demo" gate. When the binary is built with <c>-p:DemoTrialDays=N</c> (see
/// <c>Version.targets</c> and <c>build-demo.ps1</c>), <see cref="BuildInfo.DemoTrialDays"/> is a
/// positive number and the app is usable only for <c>N</c> days after its build date
/// (<see cref="BuildInfo.BuildDate"/>). A perpetual build ships with <c>DemoTrialDays == 0</c> and
/// this whole subsystem is inert.
///
/// <para><b>Clock-rollback hardening.</b> The naive check — "is today past build+N?" — is defeated by
/// winding the system clock back. To stop that casual bypass we keep a monotonic <i>high-water mark</i>
/// of the latest date the app has ever seen, in a small tamper-evident file under
/// <see cref="AppPaths.Root"/>. All expiry math uses <c>max(today, highWaterMark)</c>, so setting the
/// clock back never buys extra days; and a gross rollback (today far behind the mark) is treated as
/// expired outright. This is casual-bypass protection, not DRM: a determined user who deletes the state
/// file <i>and</i> rewinds the clock resets the window. Real enforcement needs a license/time server.</para>
/// </summary>
public static class DemoGuard
{
    /// <summary>True for a time-limited demo build.</summary>
    public static bool IsDemo => BuildInfo.DemoTrialDays > 0;

    /// <summary>Configured demo length in days (0 = perpetual build).</summary>
    public static int TrialDays => BuildInfo.DemoTrialDays;

    /// <summary>A gross backward clock jump beyond this many days is treated as tampering (and expires
    /// the demo). Small tolerance absorbs benign timezone/DST shifts across a day boundary.</summary>
    private const int RollbackToleranceDays = 2;

    /// <summary>Keyed into the state file's integrity tag so the stored date can't be hand-edited
    /// without also recomputing the tag (which needs this salt). Not a security boundary — see the
    /// class remarks — just friction against a one-line edit of a plaintext date.</summary>
    private const string StateSalt = "antiAI-ECG-Simulator::demo-hwm::v1";

    private static string StateFile => Path.Combine(AppPaths.Root, ".demostate");

    /// <summary>
    /// Evaluate the demo window and advance the high-water mark. Has a side effect (writes the state
    /// file) so call it once at startup and reuse the returned <see cref="DemoStatus"/>. For a
    /// non-demo build it returns immediately with <see cref="DemoStatus.IsDemo"/> = false and does no IO.
    /// Never throws.
    /// </summary>
    public static DemoStatus Evaluate()
    {
        if (!IsDemo)
            return new DemoStatus(false, false, 0, default, default, 0);

        var today = DateOnly.FromDateTime(DateTime.Now);

        // If the build date can't be parsed, fail OPEN (don't lock a legitimate user out over a
        // stamping bug) but still report demo mode so the title reflects it.
        if (!DateOnly.TryParseExact(BuildInfo.BuildDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var buildDate))
            return new DemoStatus(true, false, TrialDays, today, today.AddDays(TrialDays), TrialDays);

        var expiryDate = buildDate.AddDays(TrialDays);

        // High-water mark: the furthest date the app has ever observed. Missing/corrupt state simply
        // reads as "no prior sighting" (fresh), so first run and a wiped file both behave normally —
        // only a VALID stored date that sits in the future relative to now signals a rollback.
        var lastSeen = ReadHighWaterMark();
        var tampered = lastSeen is DateOnly seen
                       && seen.DayNumber - today.DayNumber > RollbackToleranceDays;

        // Monotonic "now": the clock can't run backwards from the app's point of view.
        var effectiveToday = lastSeen is DateOnly s && s > today ? s : today;

        WriteHighWaterMark(effectiveToday);

        var daysRemaining = expiryDate.DayNumber - effectiveToday.DayNumber;
        var expired = tampered || effectiveToday > expiryDate;

        return new DemoStatus(true, expired, daysRemaining, buildDate, expiryDate, TrialDays);
    }

    // --- Tamper-evident high-water-mark persistence (best-effort, never throws) ---------------------

    private static DateOnly? ReadHighWaterMark()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var raw = File.ReadAllText(StateFile).Trim();
            var dot = raw.IndexOf('.');
            if (dot <= 0 || dot >= raw.Length - 1) return null;

            var dateStr = Encoding.UTF8.GetString(Convert.FromBase64String(raw[..dot]));
            var tag = raw[(dot + 1)..];
            if (!string.Equals(tag, Tag(dateStr), StringComparison.OrdinalIgnoreCase))
                return null; // integrity check failed → ignore (treated as no prior sighting)

            return DateOnly.TryParseExact(dateStr, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteHighWaterMark(DateOnly date)
    {
        try
        {
            AppPaths.EnsureRoot();
            var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(dateStr));
            File.WriteAllText(StateFile, $"{payload}.{Tag(dateStr)}");
        }
        catch
        {
            // best-effort: a demo that can't persist its mark just falls back to trusting the OS clock
        }
    }

    private static string Tag(string dateStr)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(StateSalt));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(dateStr)));
    }
}
