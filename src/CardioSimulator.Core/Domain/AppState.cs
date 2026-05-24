namespace CardioSimulator.Core.Domain;

/// <summary>Supported UI languages with BCP-47 primary tags.</summary>
public enum Language
{
    EN,
    RU,
    ZH,
    ES,
}

public static class Languages
{
    public static readonly IReadOnlyList<Language> All = Enum.GetValues<Language>();

    public static string Tag(this Language language) => language switch
    {
        Language.EN => "en",
        Language.RU => "ru",
        Language.ZH => "zh",
        Language.ES => "es",
        _ => "en",
    };

    public static string DisplayName(this Language language) => language switch
    {
        Language.EN => "English",
        Language.RU => "Русский",
        Language.ZH => "中文",
        Language.ES => "Español",
        _ => "English",
    };

    /// <summary>Resolves a language from a (possibly region-qualified) tag, e.g. "ru-RU" → RU.</summary>
    public static Language? FromTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var dash = tag.IndexOf('-');
        var primary = (dash >= 0 ? tag.Substring(0, dash) : tag).ToLowerInvariant();
        foreach (var language in All)
        {
            if (language.Tag() == primary) return language;
        }
        return null;
    }
}

/// <summary>
/// Mutable application state: the selected operating mode, language and TCP
/// target. Defaults mirror the Android app (192.168.1.100:8080).
/// </summary>
public sealed class AppStateModel
{
    public IReadOnlyList<OperatingModeModel> OperatingModes { get; }

    public OperatingModeModel SelectedOperatingMode { get; private set; }
    public Language SelectedLanguage { get; private set; }
    public string TcpIp { get; private set; }
    public int TcpPort { get; private set; }

    public AppStateModel(
        OperatingModeModel initialOperatingMode,
        IReadOnlyList<OperatingModeModel> operatingModes,
        Language initialLanguage = Language.EN,
        string initialTcpIp = "192.168.1.100",
        int initialTcpPort = 8080)
    {
        SelectedOperatingMode = initialOperatingMode;
        OperatingModes = operatingModes;
        SelectedLanguage = initialLanguage;
        TcpIp = initialTcpIp;
        TcpPort = initialTcpPort;
    }

    public void UpdateMode(OperatingModeModel newMode) => SelectedOperatingMode = newMode;

    public void UpdateLanguage(Language newLanguage) => SelectedLanguage = newLanguage;

    public void UpdateTcpConnection(string ip, int port)
    {
        TcpIp = ip;
        TcpPort = port;
    }
}
