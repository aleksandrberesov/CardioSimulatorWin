using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
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
        Unloaded += (_, _) => _appVm.PropertyChanged -= OnAppChanged;
    }

    private UIElement BuildContent()
    {
        var panel = new StackPanel { Spacing = 14, Width = 620 };

        panel.Children.Add(SectionTitle(AppStrings.SettingsColorScheme));
        panel.Children.Add(ThemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsGridScheme));
        panel.Children.Add(GridSchemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsLanguage));
        panel.Children.Add(LanguageChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsTcpTitle));
        panel.Children.Add(TcpSection());
        panel.Children.Add(SectionTitle(AppStrings.DataSourceTitle));
        panel.Children.Add(EcgDataButtons());
        panel.Children.Add(SectionTitle(AppStrings.CourseDataTitle));
        panel.Children.Add(CourseDataButtons());
        panel.Children.Add(SectionTitle(AppStrings.Settings3DModelTitle));
        panel.Children.Add(ModelSection());

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
        _connectButton.VerticalAlignment = VerticalAlignment.Center;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(_connectingRing);
        status.Children.Add(_statusDot);
        status.Children.Add(_statusText);

        var ipStack = new StackPanel { Spacing = 2 };
        ipStack.Children.Add(_ipBox);
        ipStack.Children.Add(_ipError);

        var portStack = new StackPanel { Spacing = 2 };
        portStack.Children.Add(_portBox);
        portStack.Children.Add(_portError);

        // Address fields on one row, the connect toggle + status indicator below, so the
        // section always fits the dialog width regardless of language/button text length.
        var fieldsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        fieldsRow.Children.Add(ipStack);
        fieldsRow.Children.Add(portStack);

        var connectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        connectRow.Children.Add(_connectButton);
        connectRow.Children.Add(status);

        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(fieldsRow);
        column.Children.Add(connectRow);
        return column;
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
        var export = new Button { Content = AppStrings.DataSourceExportZip };
        export.Click += async (_, _) =>
        {
            var file = await _pickSaveZip("ecg_export");
            if (file is not null) await _appVm.ExportZipAsync(file.Path);
        };
        return TwoButtonRow(change, export);
    }

    private UIElement CourseDataButtons()
    {
        var changeCourses = new Button { Content = AppStrings.CourseChangeZip };
        changeCourses.Click += async (_, _) =>
        {
            _requestClose();
            var file = await _pickOpenZip();
            if (file is not null) await _appVm.SetCourseFolderAsync(file);
        };
        var exportCourses = new Button { Content = AppStrings.CourseExportZip };
        exportCourses.Click += async (_, _) =>
        {
            var file = await _pickSaveZip("course");
            if (file is not null) await _appVm.ExportCoursesZipAsync(file.Path);
        };
        return TwoButtonRow(changeCourses, exportCourses);
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
        if (e.PropertyName == nameof(AppViewModel.TcpConnectionState)) UpdateTcpStatus();
    }

    private void UpdateTcpStatus()
    {
        Color color;
        string text;
        var connected = false;
        switch (_appVm.TcpConnectionState)
        {
            case TcpState.Connected:
                color = Colors.Green;
                text = AppStrings.TcpStatusConnected;
                connected = true;
                break;
            case TcpState.Connecting:
                color = Colors.Gray;
                text = AppStrings.TcpStatusConnecting;
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
        _connectButton.Content = connected ? AppStrings.TcpDisconnect : AppStrings.TcpConnect;
    }
}
