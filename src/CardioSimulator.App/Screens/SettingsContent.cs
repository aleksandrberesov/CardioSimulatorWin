using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using TcpState = CardioSimulator.Core.Network.TcpConnectionState;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Settings dialog content: theme / grid-scheme / language pickers, the TCP target with a
/// connect toggle + status indicator, and change-/export-ZIP actions. Port of the Android
/// <c>SettingsContent</c>, wired to <see cref="AppViewModel"/> + <see cref="MonitorViewModel"/>.
/// </summary>
public sealed class SettingsContent : UserControl
{
    private readonly AppViewModel _appVm;
    private readonly MonitorViewModel _monitorVm;
    private readonly Func<Task<StorageFile?>> _pickOpenZip;
    private readonly Func<string, Task<StorageFile?>> _pickSaveZip;
    private readonly Action _requestClose;

    private readonly TextBox _ipBox = new();
    private readonly TextBox _portBox = new();
    private readonly Ellipse _statusDot = new() { Width = 12, Height = 12 };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _connectButton = new();
    private readonly TextBlock _ipError = new() { Foreground = new SolidColorBrush(Colors.Red), FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly TextBlock _portError = new() { Foreground = new SolidColorBrush(Colors.Red), FontSize = 11, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _connectingRing = new() { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _modelLabel = new() { FontSize = 12, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap };

    // Mirrors the Android IP-validation regex: 4 (optionally dot-separated) 0–255 octets.
    private static readonly System.Text.RegularExpressions.Regex IpRegex =
        new(@"^((25[0-5]|(2[0-4]|1\d|[1-9]|)\d)\.?\b){4}$");
    private DispatcherQueueTimer? _debounce;
    private string _pendingIp = string.Empty;
    private int _pendingPort;

    // Administrator section (Full edition only): _adminHost is fully repopulated on every role change
    // (fresh controls, no shared fields — safe to clear/rebuild). The four block-gated section hosts
    // are toggled by Visibility rather than re-parented, so their shared controls (the TCP fields)
    // are never moved between trees.
    private readonly StackPanel _adminHost = new() { Spacing = 10 };
    private FrameworkElement? _tcpSectionHost;
    private FrameworkElement? _ecgSectionHost;
    private FrameworkElement? _courseSectionHost;
    private FrameworkElement? _modelSectionHost;

    public SettingsContent(
        AppViewModel appVm,
        MonitorViewModel monitorVm,
        Func<Task<StorageFile?>> pickOpenZip,
        Func<string, Task<StorageFile?>> pickSaveZip,
        Action requestClose)
    {
        _appVm = appVm;
        _monitorVm = monitorVm;
        _pickOpenZip = pickOpenZip;
        _pickSaveZip = pickSaveZip;
        _requestClose = requestClose;

        Content = BuildContent();
        UpdateTcpStatus();
        _appVm.PropertyChanged += OnAppChanged;
    }

    /// <summary>
    /// Drops the <see cref="AppViewModel"/> subscription. The owning dialog must call this from
    /// <c>ContentDialog.Closed</c>.
    /// <para>
    /// Deliberately not wired to <c>Unloaded</c>: ContentDialog raises Unloaded on its content
    /// about 50ms after Loaded — twice, with no matching Loaded afterwards — while the dialog is
    /// still open and interactive. Detaching there left the TCP indicator and connect button frozen
    /// for the life of the dialog, so the state appeared to refresh only on close/reopen (which
    /// re-runs the ctor and re-reads the state directly).
    /// </para>
    /// </summary>
    public void Detach() => _appVm.PropertyChanged -= OnAppChanged;

    private UIElement BuildContent()
    {
        // Right padding keeps the controls clear of the ScrollViewer's overlay scrollbar, which
        // otherwise draws on top of their right edge (and widens further on hover).
        var panel = new StackPanel { Spacing = 14, Width = 620, Padding = new Thickness(0, 0, 16, 0) };

        panel.Children.Add(SectionTitle(AppStrings.SettingsColorScheme));
        panel.Children.Add(ThemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsGridScheme));
        panel.Children.Add(GridSchemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsLanguage));
        panel.Children.Add(LanguageChips());

        // The TCP / ECG-data / course-data / 3D-model sections are hideable from students via the
        // Administrator block toggles (Full edition). Each lives in its own host whose Visibility we
        // flip in ApplyBlockVisibility — no re-parenting, so the shared TCP fields stay put.
        _tcpSectionHost = SectionBlock(AppStrings.SettingsTcpTitle, TcpSection());
        panel.Children.Add(_tcpSectionHost);

        // The limited (student) edition strips the ECG-data and course-data import/export sections at
        // compile time; the Full edition can additionally hide them per-machine. AppEdition.IsLimited
        // is a compile-time const, so this block folds away in the limited build.
#pragma warning disable CS0162 // Unreachable code is intentional: edition-gated by a const flag.
        if (!AppEdition.IsLimited)
        {
            _ecgSectionHost = SectionBlock(AppStrings.DataSourceTitle, EcgDataButtons());
            panel.Children.Add(_ecgSectionHost);
            _courseSectionHost = SectionBlock(AppStrings.CourseDataTitle, CourseDataButtons());
            panel.Children.Add(_courseSectionHost);
        }
#pragma warning restore CS0162

        _modelSectionHost = SectionBlock(AppStrings.Settings3DModelTitle, ModelSection());
        panel.Children.Add(_modelSectionHost);

        panel.Children.Add(SectionTitle(AppStrings.SettingsAbout));
        panel.Children.Add(AboutSection());

        // Administrator (Full edition only): the runtime User/Admin switch + the hide/show config.
        // Folds out of the Limited build via the AppEdition.IsFull const.
#pragma warning disable CS0162 // Unreachable code is intentional: edition-gated by a const flag.
        if (AppEdition.IsFull)
        {
            panel.Children.Add(_adminHost);
            RebuildAdminSection();
        }
#pragma warning restore CS0162

        ApplyBlockVisibility();

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560,
        };
    }

    private static TextBlock SectionTitle(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15 };

    private static TextBlock SubHeading(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>A section title stacked over its content, kept in one host so its Visibility can be
    /// flipped as a unit (used for the block-gated sections).</summary>
    private static StackPanel SectionBlock(string title, UIElement content)
    {
        var block = new StackPanel { Spacing = 14 };
        block.Children.Add(SectionTitle(title));
        block.Children.Add(content);
        return block;
    }

    private static TextBlock InlineStatus() => new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Foreground = new SolidColorBrush(Colors.Red),
    };

    // ── Administrator section ──────────────────────────────────────────────

    private void ApplyBlockVisibility()
    {
        SetHostVisible(_tcpSectionHost, _appVm.IsBlockVisible(AppBlock.SettingsTcp));
        SetHostVisible(_ecgSectionHost, _appVm.IsBlockVisible(AppBlock.SettingsEcgData));
        SetHostVisible(_courseSectionHost, _appVm.IsBlockVisible(AppBlock.SettingsCourseData));
        SetHostVisible(_modelSectionHost, _appVm.IsBlockVisible(AppBlock.Settings3DModel));
    }

    private static void SetHostVisible(FrameworkElement? host, bool visible)
    {
        if (host is not null) host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Repopulates the Administrator host for the current role — the PIN login when in User
    /// mode, the switch + visibility checklists when in Admin. Called on build and on every role change.</summary>
    private void RebuildAdminSection()
    {
        _adminHost.Children.Clear();
        _adminHost.Children.Add(SectionTitle(AppStrings.SettingsAdminTitle));
        if (_appVm.Role == AppRole.Admin) BuildAdminConfig(_adminHost);
        else BuildAdminLogin(_adminHost);
    }

    /// <summary>User-mode view: enter the PIN (or set one the first time) to unlock Admin.</summary>
    private void BuildAdminLogin(StackPanel host)
    {
        var status = InlineStatus();
        var pin = new PasswordBox { PlaceholderText = AppStrings.AdminPinPlaceholder, Width = 220 };

        if (_appVm.HasAdminPin)
        {
            host.Children.Add(new TextBlock { Text = AppStrings.AdminLoginPrompt, TextWrapping = TextWrapping.Wrap });
            var enter = new Button { Content = AppStrings.AdminEnter };
            enter.Click += (_, _) =>
            {
                // On success the Role change rebuilds this section into the admin config view.
                if (!_appVm.EnterAdmin(pin.Password))
                {
                    status.Text = AppStrings.AdminPinWrong;
                    status.Visibility = Visibility.Visible;
                }
            };
            var col = new StackPanel { Spacing = 6 };
            col.Children.Add(pin);
            col.Children.Add(enter);
            host.Children.Add(col);
        }
        else
        {
            host.Children.Add(new TextBlock { Text = AppStrings.AdminSetPinPrompt, TextWrapping = TextWrapping.Wrap });
            var confirm = new PasswordBox { PlaceholderText = AppStrings.AdminConfirmPinPlaceholder, Width = 220 };
            var enable = new Button { Content = AppStrings.AdminSetPinEnable };
            enable.Click += (_, _) =>
            {
                if (!TryReadNewPin(pin, confirm, status, out var value)) return;
                _appVm.SetAdminPin(value);
                _appVm.EnterAdmin(value); // becomes Admin → Role change rebuilds this section
            };
            var col = new StackPanel { Spacing = 6 };
            col.Children.Add(pin);
            col.Children.Add(confirm);
            col.Children.Add(enable);
            host.Children.Add(col);
        }
        host.Children.Add(status);
    }

    /// <summary>Admin-mode view: drop back to User, change the PIN, and toggle per-screen / per-block
    /// visibility.</summary>
    private void BuildAdminConfig(StackPanel host)
    {
        var toUser = new Button { Content = AppStrings.AdminSwitchToUser };
        toUser.Click += (_, _) => _appVm.ExitAdmin(); // Role change rebuilds this section
        host.Children.Add(toUser);
        host.Children.Add(ChangePinExpander());

        host.Children.Add(SubHeading(AppStrings.AdminVisibleScreens));
        foreach (var mode in _appVm.OperatingModes)
        {
            var id = mode.Id;
            var cb = new CheckBox
            {
                Content = AppStrings.ModeName(id),
                IsChecked = !_appVm.IsModeHidden(id),
                IsEnabled = id.IsHideable(), // Teaching is the fixed home screen — always on
            };
            cb.Checked += (_, _) => _appVm.SetModeHidden(id, false);
            cb.Unchecked += (_, _) => _appVm.SetModeHidden(id, true);
            host.Children.Add(cb);
        }

        host.Children.Add(SubHeading(AppStrings.AdminVisibleFeatures));
        foreach (var block in AppBlocks.All)
        {
            var b = block;
            var cb = new CheckBox
            {
                Content = BlockLabel(b),
                IsChecked = !_appVm.IsBlockHidden(b),
            };
            // Re-apply so the affected section shows/hides live (a no-op while in Admin, where every
            // block stays visible; it takes visible effect once the admin switches to User mode).
            cb.Checked += (_, _) => { _appVm.SetBlockHidden(b, false); ApplyBlockVisibility(); };
            cb.Unchecked += (_, _) => { _appVm.SetBlockHidden(b, true); ApplyBlockVisibility(); };
            host.Children.Add(cb);
        }
    }

    private UIElement ChangePinExpander()
    {
        var pin = new PasswordBox { PlaceholderText = AppStrings.AdminPinPlaceholder, Width = 220 };
        var confirm = new PasswordBox { PlaceholderText = AppStrings.AdminConfirmPinPlaceholder, Width = 220 };
        var status = InlineStatus();
        var save = new Button { Content = AppStrings.CommonSave };
        save.Click += (_, _) =>
        {
            if (!TryReadNewPin(pin, confirm, status, out var value)) return;
            _appVm.SetAdminPin(value);
            pin.Password = string.Empty;
            confirm.Password = string.Empty;
            status.Foreground = new SolidColorBrush(Colors.Green);
            status.Text = AppStrings.AdminPinChanged;
            status.Visibility = Visibility.Visible;
        };

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(pin);
        body.Children.Add(confirm);
        body.Children.Add(save);
        body.Children.Add(status);
        return new Expander { Header = AppStrings.AdminChangePin, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>Validates a new PIN + confirmation, writing the reason into <paramref name="status"/>
    /// (and showing it) on failure. Resets the status colour to the error red on entry.</summary>
    private static bool TryReadNewPin(PasswordBox pin, PasswordBox confirm, TextBlock status, out string value)
    {
        value = pin.Password;
        status.Foreground = new SolidColorBrush(Colors.Red);
        if (string.IsNullOrEmpty(value))
        {
            status.Text = AppStrings.AdminPinEmpty;
            status.Visibility = Visibility.Visible;
            return false;
        }
        if (value != confirm.Password)
        {
            status.Text = AppStrings.AdminPinMismatch;
            status.Visibility = Visibility.Visible;
            return false;
        }
        status.Visibility = Visibility.Collapsed;
        return true;
    }

    private static string BlockLabel(AppBlock block) => block switch
    {
        AppBlock.SettingsEcgData => AppStrings.DataSourceTitle,
        AppBlock.SettingsCourseData => AppStrings.CourseDataTitle,
        AppBlock.SettingsTcp => AppStrings.SettingsTcpTitle,
        AppBlock.Settings3DModel => AppStrings.Settings3DModelTitle,
        _ => block.ToString(),
    };

    private static StackPanel Row() =>
        new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private UIElement ThemeChips()
    {
        var row = Row();
        var light = new RadioButton { Content = AppStrings.ThemeLight, GroupName = "theme", IsChecked = !_appVm.IsDarkTheme };
        var dark = new RadioButton { Content = AppStrings.ThemeDark, GroupName = "theme", IsChecked = _appVm.IsDarkTheme };
        light.Checked += (_, _) => _appVm.UpdateDarkTheme(false);
        dark.Checked += (_, _) => _appVm.UpdateDarkTheme(true);
        row.Children.Add(light);
        row.Children.Add(dark);
        return row;
    }

    private UIElement GridSchemeChips()
    {
        var row = Row();
        var blank = _monitorVm.MonitorMode.BlankSheet;

        foreach (var scheme in new[] { GridScheme.Yellow, GridScheme.BlueGray, GridScheme.Pink })
        {
            var captured = scheme;
            var rb = new RadioButton
            {
                Content = AppStrings.GridSchemeLabel(scheme),
                GroupName = "grid",
                // A grid scheme is selected only when blank-sheet is off.
                IsChecked = !blank && _monitorVm.MonitorMode.GridScheme == scheme,
            };
            rb.Checked += (_, _) =>
            {
                _monitorVm.SetBlankSheet(false);
                _monitorVm.SetGridScheme(captured);
            };
            row.Children.Add(rb);
        }

        // Fourth option: the "bedside monitor" sheet (green trace on black, no grid).
        var blankRb = new RadioButton
        {
            Content = AppStrings.GridSchemeBedside,
            GroupName = "grid",
            IsChecked = blank,
        };
        blankRb.Checked += (_, _) => _monitorVm.SetBlankSheet(true);
        row.Children.Add(blankRb);

        return row;
    }

    private UIElement LanguageChips()
    {
        var row = Row();
        foreach (var language in Languages.All)
        {
            var captured = language;
            var rb = new RadioButton
            {
                Content = language.DisplayName(),
                GroupName = "lang",
                IsChecked = _appVm.SelectedLanguage == language,
            };
            rb.Checked += (_, _) => _appVm.UpdateLanguage(captured);
            row.Children.Add(rb);
        }
        return row;
    }

    private UIElement TcpSection()
    {
        _ipBox.Header = AppStrings.SettingsTcpIp;
        _ipBox.Text = _appVm.TcpIp;
        _ipBox.Width = 160;
        _ipBox.TextChanged += OnTcpFieldChanged;
        _ipError.Text = AppStrings.SettingsTcpIpError;

        _portBox.Header = AppStrings.SettingsTcpPort;
        _portBox.Text = _appVm.TcpPort.ToString();
        _portBox.Width = 90;
        _portBox.TextChanged += OnTcpFieldChanged;
        _portError.Text = AppStrings.SettingsTcpPortError;

        _connectButton.Click += OnConnectClick;
        // Bottom-aligned so the button lines up with the text boxes themselves rather than
        // floating against their headers.
        _connectButton.VerticalAlignment = VerticalAlignment.Bottom;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        status.Children.Add(_connectingRing);
        status.Children.Add(_statusDot);
        status.Children.Add(_statusText);
        // The status text is the only elastic cell; an Error state interpolates a socket message
        // of unbounded length, so ellipsize rather than let it push the row past the dialog edge.
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;

        // Address fields, connect toggle and status indicator all share the top row. The two
        // validation labels get their own row beneath their field, so showing one grows the
        // section downwards instead of disturbing the row's alignment.
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(_ipBox, 0);
        Grid.SetColumn(_portBox, 1);
        Grid.SetColumn(_connectButton, 2);
        Grid.SetColumn(status, 3);
        Grid.SetRow(_ipError, 1);
        Grid.SetColumn(_ipError, 0);
        Grid.SetRow(_portError, 1);
        Grid.SetColumn(_portError, 1);

        grid.Children.Add(_ipBox);
        grid.Children.Add(_portBox);
        grid.Children.Add(_connectButton);
        grid.Children.Add(status);
        grid.Children.Add(_ipError);
        grid.Children.Add(_portError);
        return grid;
    }

    private void OnTcpFieldChanged(object sender, TextChangedEventArgs e)
    {
        var ip = (_ipBox.Text ?? string.Empty).Trim();
        var portText = (_portBox.Text ?? string.Empty).Trim();

        var ipValid = ip.Length == 0 || IpRegex.IsMatch(ip);
        _ipError.Visibility = ip.Length > 0 && !ipValid ? Visibility.Visible : Visibility.Collapsed;

        var portValid = portText.Length == 0 ||
            (int.TryParse(portText, out var p) && p >= 0 && p <= 65535);
        _portError.Visibility = portText.Length > 0 && !portValid ? Visibility.Visible : Visibility.Collapsed;

        // Debounced persist of a valid target (mirrors the Android 1s LaunchedEffect).
        if (ipValid && portValid && ip.Length > 0 && portText.Length > 0)
        {
            _pendingIp = ip;
            _pendingPort = int.Parse(portText);
            _debounce ??= CreateDebounce();
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private DispatcherQueueTimer CreateDebounce()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1000);
        timer.Tick += (s, _) => { s.Stop(); _appVm.UpdateTcpConnection(_pendingIp, _pendingPort); };
        return timer;
    }

    private UIElement EcgDataButtons()
    {
        var change = new Button { Content = AppStrings.DataSourceChangeFolder };
        change.Click += async (_, _) =>
        {
            _requestClose();
            var file = await _pickOpenZip();
            if (file is not null) await _appVm.SetDataFolderAsync(file);
        };
        var reset = new Button { Content = AppStrings.DataSourceResetDefault };
        reset.Click += async (_, _) =>
        {
            await _appVm.ResetDataFolderToDefaultAsync();
        };
        var export = new Button { Content = AppStrings.DataSourceExportZip };
        export.Click += async (_, _) =>
            await RunExportAsync(AppStrings.DataSourceExportZip, "ecg_export.pak",
                (path, progress, ct) => _appVm.ExportZipAsync(path, progress, ct));
        return ThreeButtonRow(change, reset, export);
    }

    private UIElement CourseDataButtons()
    {
        var changeCourses = new Button { Content = AppStrings.CourseChangeZip };
        changeCourses.Click += async (_, _) =>
        {
            _requestClose();
            var file = await _pickOpenZip();
            if (file is null) return;

            // Show the load progress + result on the main window: this control's dialog is closing, so
            // its XamlRoot is going away. Fall back to a silent load if the root can't be resolved.
            if (App.MainWindow?.Content?.XamlRoot is { } root)
                await CourseLoadReportDialog.ShowAsync(root, _appVm, file);
            else
                await _appVm.SetCourseFolderAsync(file);
        };
        var resetCourses = new Button { Content = AppStrings.CourseResetDefault };
        resetCourses.Click += async (_, _) =>
        {
            await _appVm.ResetCourseFolderToDefaultAsync();
        };
        var exportCourses = new Button { Content = AppStrings.CourseExportZip };
        exportCourses.Click += async (_, _) =>
            await RunExportAsync(AppStrings.CourseExportZip, "course.pak",
                (path, progress, ct) => _appVm.ExportCoursesZipAsync(path, progress, ct));
        return ThreeButtonRow(changeCourses, resetCourses, exportCourses);
    }

    /// <summary>
    /// Closes this Settings dialog, prompts for a destination, then runs <paramref name="export"/> under
    /// a cancellable progress dialog on the main window. Order matters: the Settings host is itself a
    /// <see cref="ContentDialog"/> and WinUI shows only one at a time, so we <see cref="_requestClose"/>
    /// first and let the save-picker's own <c>await</c> cover the close transition before the progress
    /// dialog opens — the same hand-off the course-import flow uses. Falls back to a silent export if the
    /// main window's root can't be resolved.
    /// </summary>
    private async Task RunExportAsync(
        string title,
        string suggestedName,
        Func<string, IProgress<ExportProgress>, CancellationToken, Task<ExportOutcome>> export)
    {
        _requestClose();
        var file = await _pickSaveZip(suggestedName);
        if (file is null) return;

        if (App.MainWindow?.Content?.XamlRoot is { } root)
            await ExportProgressDialog.ShowAsync(root, title, (progress, ct) => export(file.Path, progress, ct));
        else
            await export(file.Path, new Progress<ExportProgress>(), System.Threading.CancellationToken.None);
    }

    /// <summary>
    /// 3D heart model picker: choose a model file (copied into user storage as the override the 3D
    /// viewer loads) or reset to the bundled default. Persists across sessions; the change takes
    /// effect the next time the 3D view is opened.
    /// </summary>
    private UIElement ModelSection()
    {
        var change = new Button { Content = AppStrings.Monitor3DLoadModel };
        change.Click += OnChangeModelClick;
        var reset = new Button { Content = AppStrings.Settings3DModelReset };
        reset.Click += (_, _) =>
        {
            HeartModelStore.ResetToBundled();
            _modelLabel.Text = AppStrings.Settings3DModelDefault;
        };

        _modelLabel.Text = HeartModelStore.HasUserModel()
            ? AppStrings.Settings3DModelCustom
            : AppStrings.Settings3DModelDefault;

        var column = new StackPanel { Spacing = 6 };
        column.Children.Add(TwoButtonRow(change, reset));
        column.Children.Add(_modelLabel);
        return column;
    }

    /// <summary>
    /// "About" block: the app name plus the auto-incrementing build version and build date, read
    /// from the generated <see cref="BuildInfo"/> (regenerated on every build — see Version.targets).
    /// </summary>
    private static UIElement AboutSection()
    {
        var column = new StackPanel { Spacing = 2 };
        column.Children.Add(new TextBlock
        {
            Text = BuildInfo.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
        });
        column.Children.Add(new TextBlock
        {
            Text = $"{AppStrings.AboutVersion} {BuildInfo.FullVersion}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.Gray),
        });
        column.Children.Add(new TextBlock
        {
            Text = $"{AppStrings.AboutBuilt} {BuildInfo.BuildDate}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray),
        });
        return column;
    }

    private async void OnChangeModelClick(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in HeartModelStore.Extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            HeartModelStore.SaveUserModel(file.Path);
            _modelLabel.Text = file.Name;
        }
        catch (Exception ex)
        {
            _modelLabel.Text = $"{AppStrings.Monitor3DLoadFailed}: {ex.Message}";
        }
    }

    // Two equal-width, stretched buttons sharing a row — keeps long localized labels
    // inside the dialog instead of overflowing off the edge.
    private static Grid TwoButtonRow(Button left, Button right)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left.HorizontalAlignment = HorizontalAlignment.Stretch;
        right.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    // Three equal-width, stretched buttons sharing a row.
    private static Grid ThreeButtonRow(Button left, Button middle, Button right)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left.HorizontalAlignment = HorizontalAlignment.Stretch;
        middle.HorizontalAlignment = HorizontalAlignment.Stretch;
        right.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);
        return grid;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var ip = (_ipBox.Text ?? string.Empty).Trim();
        if (!int.TryParse(_portBox.Text, out var port) || port < 0 || port > 65535)
        {
            _statusText.Text = $"{AppStrings.TcpStatusError}: port";
            return;
        }
        _appVm.UpdateTcpConnection(ip, port);
        _appVm.ToggleTcpConnection();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.TcpConnectionState))
        {
            UpdateTcpStatus();
        }
        else if (e.PropertyName == nameof(AppViewModel.Role))
        {
            // Entering/leaving Admin swaps the section between the PIN login and the config checklists,
            // and the block-gated sections reappear in Admin (IsBlockVisible is always true there).
            RebuildAdminSection();
            ApplyBlockVisibility();
        }
    }

    private void UpdateTcpStatus()
    {
        Color color;
        string text;
        // "Active" (not merely Connected) must mirror ToggleTcpConnection's own split: it treats
        // anything that is not Disconnected/Error as something to tear down. Connecting counts,
        // otherwise the button reads "Connect" while a retry loop is in flight and actually
        // aborts it — and against a dead host the loop cycles Connecting/Disconnected forever
        // with the label never leaving "Connect".
        var active = false;
        switch (_appVm.TcpConnectionState)
        {
            case TcpState.Connected:
                color = Colors.Green;
                text = AppStrings.TcpStatusConnected;
                active = true;
                break;
            case TcpState.Connecting:
                color = Colors.Gray;
                text = AppStrings.TcpStatusConnecting;
                active = true;
                break;
            case TcpState.Error error:
                color = Colors.Magenta;
                text = $"{AppStrings.TcpStatusError}: {error.Message}";
                break;
            default:
                color = Colors.Red;
                text = AppStrings.TcpStatusDisconnected;
                break;
        }
        var connecting = _appVm.TcpConnectionState is TcpState.Connecting;
        _connectingRing.IsActive = connecting;
        _connectingRing.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        _statusDot.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
        _statusDot.Fill = new SolidColorBrush(color);
        _statusText.Text = text;
        _connectButton.Content = active ? AppStrings.TcpDisconnect : AppStrings.TcpConnect;
    }
}
