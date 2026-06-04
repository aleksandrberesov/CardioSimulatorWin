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
    private const string KeyLastOperatingMode = "last_operating_mode";
    private const string KeyLastRhythmId = "last_rhythm_id";
    private const string KeyLastCourseId = "last_course_id";
    private const string KeyLastLectureId = "last_lecture_id";
    private const string KeyMonitorSpeed = "monitor_speed";
    private const string KeyMonitorScale = "monitor_scale";
    private const string KeyMonitorDisplayScale = "monitor_display_scale";
    private const string KeyMonitorSeriesCount = "monitor_series_count";
    private const string KeyMonitorSeriesScheme = "monitor_series_scheme";

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

    /// <summary>Last selected operating mode (enum name), restored on next launch.</summary> 
    public string? LastOperatingMode
    {
        get => Get(KeyLastOperatingMode);
        set => Set(KeyLastOperatingMode, value);
    }

    public string? LastRhythmId
    {
        get => Get(KeyLastRhythmId);
        set => Set(KeyLastRhythmId, value);
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

    private string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;        

    private void Set(string key, string? value)
    {
        if (value is null) _values.Remove(key);
        else _values[key] = value;
        Save();
    }

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
