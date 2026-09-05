using System.Text.Json;

namespace CardioSimulator.App.Data;

/// <summary>
/// Persistent app settings, mirroring the Android <c>DataSourcePrefs</c> (a Jetpack
/// DataStore). Backed by a small JSON file under <see cref="AppPaths.PrefsFile"/>.
/// Synchronous: the WinUI app reads/writes a handful of keys, not a stream.
/// </summary>
public sealed class DataSourcePrefs
{
    private const string KeyTreeUri = "tree_uri";
    private const string KeyCoursesTreeUri = "courses_tree_uri";
    private const string KeyLanguageTag = "language_tag";
    private const string KeyTcpIp = "tcp_ip";
    private const string KeyTcpPort = "tcp_port";
    private const string KeyDarkTheme = "dark_theme";
    private const string KeyBlankSheet = "blank_sheet";
    private const string KeyGridScheme = "grid_scheme";
    private const string KeyLastRhythmId = "last_rhythm_id";
    private const string KeyLastCourseId = "last_course_id";
    private const string KeyLastLectureId = "last_lecture_id";
    private const string KeyCourseCtorCourseId = "course_ctor_course_id";
    private const string KeyCourseCtorLectureId = "course_ctor_lecture_id";
    private const string KeyMonitorSpeed = "monitor_speed";
    private const string KeyMonitorScale = "monitor_scale";
    private const string KeyMonitorDisplayScale = "monitor_display_scale";
    private const string KeyMonitorSeriesCount = "monitor_series_count";
    private const string KeyMonitorSeriesScheme = "monitor_series_scheme";
    private const string KeyDrawerFixed = "drawer_fixed";
    private const string KeyMonitorSound = "monitor_sound";
    private const string KeyWelcomeShown = "welcome_shown";
    private const string KeyWelcomeDisabled = "welcome_disabled";
    private const string KeyAppRole = "app_role";
    private const string KeyAdminPinHash = "admin_pin_hash";
    private const string KeyAdminPinSalt = "admin_pin_salt";
    private const string KeyHiddenModes = "hidden_modes";
    private const string KeyHiddenBlocks = "hidden_blocks";

    private readonly Dictionary<string, string> _values;

    public DataSourcePrefs()
    {
        _values = Load();
    }

    /// <summary>Path of the ZIP the user last picked (Android persisted the SAF tree URI).</summary>
    public string? TreeUri
    {
        get => Get(KeyTreeUri);
        set => Set(KeyTreeUri, value);
    }

    public string? CoursesTreeUri
    {
        get => Get(KeyCoursesTreeUri);
        set => Set(KeyCoursesTreeUri, value);
    }

    public string? LanguageTag
    {
        get => Get(KeyLanguageTag);
        set => Set(KeyLanguageTag, value);
    }

    public string? TcpIp
    {
        get => Get(KeyTcpIp);
        set => Set(KeyTcpIp, value);
    }

    public int? TcpPort
    {
        get => int.TryParse(Get(KeyTcpPort), out var v) ? v : null;
        set => Set(KeyTcpPort, value?.ToString());
    }

    public bool? DarkTheme
    {
        get => bool.TryParse(Get(KeyDarkTheme), out var v) ? v : null;
        set => Set(KeyDarkTheme, value?.ToString());
    }

    public bool? BlankSheet
    {
        get => bool.TryParse(Get(KeyBlankSheet), out var v) ? v : null;
        set => Set(KeyBlankSheet, value?.ToString());
    }

    public string? GridScheme
    {
        get => Get(KeyGridScheme);
        set => Set(KeyGridScheme, value);
    }

    public string? LastRhythmId
    {
        get => Get(KeyLastRhythmId);
        set => Set(KeyLastRhythmId, value);
    }

    /// <summary>Last selected teaching course id (null/empty = All rhythms).</summary>
    public string? LastCourseId
    {
        get => Get(KeyLastCourseId);
        set => Set(KeyLastCourseId, value);
    }

    /// <summary>Last course opened in the Course Constructor (restored on next launch).</summary>
    public string? CourseCtorCourseId
    {
        get => Get(KeyCourseCtorCourseId);
        set => Set(KeyCourseCtorCourseId, value);
    }

    /// <summary>Last lecture opened in the Course Constructor (restored on next launch).</summary>
    public string? CourseCtorLectureId
    {
        get => Get(KeyCourseCtorLectureId);
        set => Set(KeyCourseCtorLectureId, value);
    }

    public float? MonitorSpeed
    {
        get => float.TryParse(Get(KeyMonitorSpeed), out var v) ? v : null;
        set => Set(KeyMonitorSpeed, value?.ToString());
    }

    public float? MonitorScale
    {
        get => float.TryParse(Get(KeyMonitorScale), out var v) ? v : null;
        set => Set(KeyMonitorScale, value?.ToString());
    }

    public float? MonitorDisplayScale
    {
        get => float.TryParse(Get(KeyMonitorDisplayScale), out var v) ? v : null;
        set => Set(KeyMonitorDisplayScale, value?.ToString());
    }

    public int? MonitorSeriesCount
    {
        get => int.TryParse(Get(KeyMonitorSeriesCount), out var v) ? v : null;
        set => Set(KeyMonitorSeriesCount, value?.ToString());
    }

    public string? MonitorSeriesScheme
    {
        get => Get(KeyMonitorSeriesScheme);
        set => Set(KeyMonitorSeriesScheme, value);
    }

    /// <summary>Whether the teaching rhythm drawer is pinned open (Android <c>isDrawerFixed</c>).</summary>
    public bool? DrawerFixed
    {
        get => bool.TryParse(Get(KeyDrawerFixed), out var v) ? v : null;
        set => Set(KeyDrawerFixed, value?.ToString());
    }

    /// <summary>Whether the monitor's R-peak pulse beep is enabled (null ⇒ default on).</summary>
    public bool? MonitorSoundEnabled
    {
        get => bool.TryParse(Get(KeyMonitorSound), out var v) ? v : null;
        set => Set(KeyMonitorSound, value?.ToString());
    }

    /// <summary>Whether the first-launch welcome screen has been dismissed (shown once).</summary>
    public bool? WelcomeShown
    {
        get => bool.TryParse(Get(KeyWelcomeShown), out var v) ? v : null;
        set => Set(KeyWelcomeShown, value?.ToString());
    }

    /// <summary>Whether the user has disabled the welcome screen on startup.</summary>
    public bool? WelcomeDisabled
    {
        get => bool.TryParse(Get(KeyWelcomeDisabled), out var v) ? v : null;
        set => Set(KeyWelcomeDisabled, value?.ToString());
    }

    // ── Admin / User runtime role (Full edition only) ──────────────────────
    // Raw strings only — the AppViewModel parses the enum name, verifies the salted PIN hash, and
    // (de)serializes the hidden-item JSON. Null in every field means "never configured" (⇒ User
    // role, no PIN, nothing hidden), i.e. today's out-of-the-box behavior.

    /// <summary>Persisted <c>AppRole</c> name (<c>"User"</c>/<c>"Admin"</c>); null ⇒ User.</summary>
    public string? AppRoleName
    {
        get => Get(KeyAppRole);
        set => Set(KeyAppRole, value);
    }

    /// <summary>Base64 SHA-256 of the admin PIN salted with <see cref="AdminPinSalt"/>; null until set.</summary>
    public string? AdminPinHash
    {
        get => Get(KeyAdminPinHash);
        set => Set(KeyAdminPinHash, value);
    }

    /// <summary>Base64 per-install random salt for the admin PIN hash; null until a PIN is set.</summary>
    public string? AdminPinSalt
    {
        get => Get(KeyAdminPinSalt);
        set => Set(KeyAdminPinSalt, value);
    }

    /// <summary>JSON array of hidden <c>OperatingMode</c> names (screens gone in User mode).</summary>
    public string? HiddenModes
    {
        get => Get(KeyHiddenModes);
        set => Set(KeyHiddenModes, value);
    }

    /// <summary>JSON array of hidden <c>AppBlock</c> names (in-screen blocks gone in User mode).</summary>
    public string? HiddenBlocks
    {
        get => Get(KeyHiddenBlocks);
        set => Set(KeyHiddenBlocks, value);
    }

    // Internal access for mode-scoped reads/writes from sibling assemblies (e.g. MonitorViewModel).
    internal string? GetRaw(string key) => _values.TryGetValue(key, out var v) ? v : null;

    internal void SetRaw(string key, string? value)
    {
        if (value is null) _values.Remove(key);
        else _values[key] = value;
        Save();
    }

    private string? Get(string key) => GetRaw(key);

    private void Set(string key, string? value) => SetRaw(key, value);

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.PrefsFile)) return new Dictionary<string, string>();    
            var json = File.ReadAllText(AppPaths.PrefsFile);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(AppPaths.PrefsFile, JsonSerializer.Serialize(_values));
        }
        catch
        {
            // best-effort persistence; ignore IO failures
        }
    }
}
