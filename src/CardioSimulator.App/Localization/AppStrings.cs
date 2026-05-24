using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Localization;

/// <summary>
/// Language-aware UI strings, ported from the Android <c>res/values*/strings.xml</c>.
/// <see cref="Current"/> selects the active language; <see cref="Changed"/> fires when it
/// switches so the UI can re-pull localized text. Unknown keys fall back to English.
/// </summary>
public static class AppStrings
{
    private static Language _current = Language.EN;

    /// <summary>Raised when <see cref="Current"/> changes (UI rebuilds on this).</summary>
    public static event Action? Changed;

    public static Language Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            Changed?.Invoke();
        }
    }

    private static string S(string key)
    {
        var table = _current switch
        {
            Language.RU => Ru,
            Language.ZH => Zh,
            Language.ES => Es,
            _ => En,
        };
        if (table.TryGetValue(key, out var value)) return value;
        return En.TryGetValue(key, out var en) ? en : key;
    }

    // Data source
    public static string DataSourceTitle => S("data_source_title");
    public static string DataSourceDescription => S("data_source_description");
    public static string DataSourcePickFolder => S("data_source_pick_folder");
    public static string DataSourceChangeFolder => S("data_source_change_folder");
    public static string DataSourceLoading => S("data_source_loading");
    public static string DataSourceContinue => S("data_source_continue");
    public static string DataSourceRetry => S("data_source_retry");
    public static string DataSourceShowDetails => S("data_source_show_details");
    public static string DataSourceClose => S("data_source_close");
    public static string DataSourceErrorBadManifest => S("data_source_error_bad_manifest");
    public static string DataSourceErrorUnreadable => S("data_source_error_unreadable");
    public static string DataSourceErrorEmpty => S("data_source_error_empty");
    public static string DataSourceExportZip => S("data_source_export_zip");
    public static string RhythmSearchPlaceholder => S("rhythm_search_placeholder");

    public static string DataSourceLoadedFormat(int count) => string.Format(S("data_source_loaded_format"), count);
    public static string DataSourcePathologiesTitle(int count) => string.Format(S("data_source_pathologies_title"), count);

    // Settings
    public static string SettingsTitle => S("settings_title");
    public static string SettingsClose => S("settings_close");
    public static string SettingsColorScheme => S("settings_color_scheme");
    public static string ThemeLight => S("theme_light");
    public static string ThemeDark => S("theme_dark");
    public static string SettingsGridScheme => S("settings_grid_scheme");
    public static string SettingsLanguage => S("settings_language");
    public static string SettingsTcpTitle => S("settings_tcp_title");
    public static string SettingsTcpIp => S("settings_tcp_ip");
    public static string SettingsTcpPort => S("settings_tcp_port");
    public static string TcpConnect => S("tcp_connect");
    public static string TcpDisconnect => S("tcp_disconnect");
    public static string TcpStatusConnected => S("tcp_status_connected");
    public static string TcpStatusConnecting => S("tcp_status_waiting");
    public static string TcpStatusDisconnected => S("tcp_status_disconnected");
    public static string TcpStatusError => S("tcp_status_error");

    public static string ModeName(OperatingMode mode) => S(mode.TitleResourceKey());
    public static string GridSchemeLabel(GridScheme scheme) => S(scheme.LabelResourceKey());

    private static readonly Dictionary<string, string> En = new()
    {
        ["data_source_title"] = "ECG Data",
        ["data_source_description"] = "Select a ZIP archive on your device containing the ECG data.",
        ["data_source_pick_folder"] = "Select ZIP archive",
        ["data_source_change_folder"] = "Change ZIP archive",
        ["data_source_loading"] = "Loading data…",
        ["data_source_continue"] = "Continue",
        ["data_source_retry"] = "Pick another ZIP",
        ["data_source_show_details"] = "Show Details",
        ["data_source_close"] = "Close",
        ["data_source_error_bad_manifest"] = "The ZIP archive contains an invalid or missing manifest file.",
        ["data_source_error_unreadable"] = "The selected ZIP archive is no longer accessible.",
        ["data_source_error_empty"] = "No ECG files were found in the selected ZIP archive.",
        ["data_source_export_zip"] = "Export ZIP",
        ["data_source_loaded_format"] = "Loaded {0} pathologies",
        ["data_source_pathologies_title"] = "Loaded Pathologies ({0})",
        ["rhythm_search_placeholder"] = "Rhythm…",
        ["settings_title"] = "Settings",
        ["settings_close"] = "CLOSE",
        ["settings_color_scheme"] = "App Theme",
        ["theme_light"] = "Light",
        ["theme_dark"] = "Dark",
        ["settings_grid_scheme"] = "Monitor Grid Scheme",
        ["settings_language"] = "Language",
        ["settings_tcp_title"] = "TCP Connection",
        ["settings_tcp_ip"] = "IP Address",
        ["settings_tcp_port"] = "Port",
        ["tcp_connect"] = "Connect",
        ["tcp_disconnect"] = "Disconnect",
        ["tcp_status_connected"] = "Connected",
        ["tcp_status_waiting"] = "Waiting…",
        ["tcp_status_disconnected"] = "Disconnected",
        ["tcp_status_error"] = "Error",
        ["mode_teaching"] = "Teaching",
        ["mode_testing"] = "Testing",
        ["mode_examination"] = "Examination",
        ["mode_oske"] = "OSKE",
        ["mode_editor"] = "Editor",
        ["grid_scheme_pink"] = "Pink",
        ["grid_scheme_blue_gray"] = "Blue/Gray",
    };

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["data_source_title"] = "Данные ЭКГ",
        ["data_source_description"] = "Выберите на устройстве ZIP-архив, содержащий данные ЭКГ.",
        ["data_source_pick_folder"] = "Выбрать ZIP-архив",
        ["data_source_change_folder"] = "Сменить ZIP-архив",
        ["data_source_loading"] = "Загрузка данных…",
        ["data_source_continue"] = "Продолжить",
        ["data_source_retry"] = "Выбрать другой архив",
        ["data_source_show_details"] = "Подробнее",
        ["data_source_close"] = "Закрыть",
        ["data_source_error_bad_manifest"] = "ZIP-архив содержит неверный или отсутствующий файл манифеста.",
        ["data_source_error_unreadable"] = "Выбранный архив больше недоступен.",
        ["data_source_error_empty"] = "В выбранном архиве не найдены файлы ЭКГ.",
        ["data_source_export_zip"] = "Экспорт ZIP",
        ["data_source_loaded_format"] = "Загружено патологий: {0}",
        ["data_source_pathologies_title"] = "Загруженные патологии ({0})",
        ["rhythm_search_placeholder"] = "Ритм…",
        ["settings_title"] = "Настройки",
        ["settings_close"] = "ЗАКРЫТЬ",
        ["settings_color_scheme"] = "Тема приложения",
        ["theme_light"] = "Светлая",
        ["theme_dark"] = "Темная",
        ["settings_grid_scheme"] = "Сетка монитора",
        ["settings_language"] = "Язык",
        ["settings_tcp_title"] = "TCP соединение",
        ["settings_tcp_ip"] = "IP-адрес",
        ["settings_tcp_port"] = "Порт",
        ["tcp_connect"] = "Подключить",
        ["tcp_disconnect"] = "Отключить",
        ["tcp_status_connected"] = "Подключено",
        ["tcp_status_waiting"] = "Ожидание…",
        ["tcp_status_disconnected"] = "Отключено",
        ["tcp_status_error"] = "Ошибка",
        ["mode_teaching"] = "Обучение",
        ["mode_testing"] = "Тестирование",
        ["mode_examination"] = "Экзамен",
        ["mode_oske"] = "ОСКЭ",
        ["mode_editor"] = "Редактор",
        ["grid_scheme_pink"] = "Розовая",
        ["grid_scheme_blue_gray"] = "Сине-серая",
    };

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["data_source_title"] = "心电图数据",
        ["data_source_description"] = "请选择设备上包含心电图数据的 ZIP 压缩包。",
        ["data_source_pick_folder"] = "选择 ZIP 压缩包",
        ["data_source_change_folder"] = "更改 ZIP 压缩包",
        ["data_source_loading"] = "正在加载数据…",
        ["data_source_continue"] = "继续",
        ["data_source_retry"] = "选择其他 ZIP",
        ["data_source_show_details"] = "显示详情",
        ["data_source_close"] = "关闭",
        ["data_source_error_bad_manifest"] = "ZIP 压缩包包含无效或缺失的清单文件。",
        ["data_source_error_unreadable"] = "所选 ZIP 压缩包已无法访问。",
        ["data_source_error_empty"] = "所选 ZIP 压缩包中未找到心电图文件。",
        ["data_source_export_zip"] = "导出 ZIP",
        ["data_source_loaded_format"] = "已加载 {0} 个病理",
        ["data_source_pathologies_title"] = "已加载病理 ({0})",
        ["rhythm_search_placeholder"] = "心律…",
        ["settings_title"] = "设置",
        ["settings_close"] = "关闭",
        ["settings_color_scheme"] = "应用主题",
        ["theme_light"] = "浅色",
        ["theme_dark"] = "深色",
        ["settings_grid_scheme"] = "监视器网格方案",
        ["settings_language"] = "语言",
        ["settings_tcp_title"] = "TCP 连接",
        ["settings_tcp_ip"] = "IP 地址",
        ["settings_tcp_port"] = "端口",
        ["tcp_connect"] = "连接",
        ["tcp_disconnect"] = "断开连接",
        ["tcp_status_connected"] = "已连接",
        ["tcp_status_waiting"] = "等待中…",
        ["tcp_status_disconnected"] = "未连接",
        ["tcp_status_error"] = "错误",
        ["mode_teaching"] = "教学",
        ["mode_testing"] = "测试",
        ["mode_examination"] = "考试",
        ["mode_oske"] = "客观结构化临床考试",
        ["mode_editor"] = "编辑器",
        ["grid_scheme_pink"] = "粉色",
        ["grid_scheme_blue_gray"] = "蓝灰色",
    };

    private static readonly Dictionary<string, string> Es = new()
    {
        ["data_source_title"] = "Datos ECG",
        ["data_source_description"] = "Selecciona en tu dispositivo un archivo ZIP que contenga los datos ECG.",
        ["data_source_pick_folder"] = "Seleccionar archivo ZIP",
        ["data_source_change_folder"] = "Cambiar archivo ZIP",
        ["data_source_loading"] = "Cargando datos…",
        ["data_source_continue"] = "Continuar",
        ["data_source_retry"] = "Elegir otro ZIP",
        ["data_source_show_details"] = "Ver Detalles",
        ["data_source_close"] = "Cerrar",
        ["data_source_error_bad_manifest"] = "El archivo ZIP contiene un archivo de manifiesto no válido o ausente.",
        ["data_source_error_unreadable"] = "El archivo ZIP seleccionado ya no está accesible.",
        ["data_source_error_empty"] = "No se encontraron archivos ECG en el archivo ZIP seleccionado.",
        ["data_source_export_zip"] = "Exportar ZIP",
        ["data_source_loaded_format"] = "Patologías cargadas: {0}",
        ["data_source_pathologies_title"] = "Patologías Cargadas ({0})",
        ["rhythm_search_placeholder"] = "Ritmo…",
        ["settings_title"] = "Ajustes",
        ["settings_close"] = "CERRAR",
        ["settings_color_scheme"] = "Tema de la aplicación",
        ["theme_light"] = "Claro",
        ["theme_dark"] = "Oscuro",
        ["settings_grid_scheme"] = "Esquema de cuadrícula",
        ["settings_language"] = "Idioma",
        ["settings_tcp_title"] = "Conexión TCP",
        ["settings_tcp_ip"] = "Dirección IP",
        ["settings_tcp_port"] = "Puerto",
        ["tcp_connect"] = "Conectar",
        ["tcp_disconnect"] = "Desconectar",
        ["tcp_status_connected"] = "Conectado",
        ["tcp_status_waiting"] = "Esperando…",
        ["tcp_status_disconnected"] = "Desconectado",
        ["tcp_status_error"] = "Error",
        ["mode_teaching"] = "Enseñanza",
        ["mode_testing"] = "Prueba",
        ["mode_examination"] = "Examen",
        ["mode_oske"] = "ECOE",
        ["mode_editor"] = "Editor",
        ["grid_scheme_pink"] = "Rosa",
        ["grid_scheme_blue_gray"] = "Azul/Gris",
    };
}
