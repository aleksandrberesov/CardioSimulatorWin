using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
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
    private readonly Func<Task<StorageFile?>> _pickSaveZip;
    private readonly Action _requestClose;

    private readonly TextBox _ipBox = new();
    private readonly TextBox _portBox = new();
    private readonly Ellipse _statusDot = new() { Width = 12, Height = 12 };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _connectButton = new();

    public SettingsContent(
        AppViewModel appVm,
        MonitorViewModel monitorVm,
        Func<Task<StorageFile?>> pickOpenZip,
        Func<Task<StorageFile?>> pickSaveZip,
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
        var panel = new StackPanel { Spacing = 14, Width = 460 };

        panel.Children.Add(SectionTitle(AppStrings.SettingsColorScheme));
        panel.Children.Add(ThemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsGridScheme));
        panel.Children.Add(GridSchemeChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsLanguage));
        panel.Children.Add(LanguageChips());
        panel.Children.Add(SectionTitle(AppStrings.SettingsTcpTitle));
        panel.Children.Add(TcpSection());
        panel.Children.Add(SectionTitle(AppStrings.DataSourceTitle));
        panel.Children.Add(DataButtons());

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
        foreach (var scheme in new[] { GridScheme.Pink, GridScheme.BlueGray })
        {
            var captured = scheme;
            var rb = new RadioButton
            {
                Content = AppStrings.GridSchemeLabel(scheme),
                GroupName = "grid",
                IsChecked = _monitorVm.MonitorMode.GridScheme == scheme,
            };
            rb.Checked += (_, _) => _monitorVm.SetGridScheme(captured);
            row.Children.Add(rb);
        }
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
        _portBox.Header = AppStrings.SettingsTcpPort;
        _portBox.Text = _appVm.TcpPort.ToString();
        _portBox.Width = 90;
        _connectButton.Click += OnConnectClick;
        _connectButton.VerticalAlignment = VerticalAlignment.Bottom;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        status.Children.Add(_statusDot);
        status.Children.Add(_statusText);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(_ipBox);
        row.Children.Add(_portBox);
        row.Children.Add(_connectButton);
        row.Children.Add(status);
        return row;
    }

    private UIElement DataButtons()
    {
        var row = Row();
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
            var file = await _pickSaveZip();
            if (file is not null) await _appVm.ExportZipAsync(file.Path);
        };
        row.Children.Add(change);
        row.Children.Add(export);
        return row;
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
        _statusDot.Fill = new SolidColorBrush(color);
        _statusText.Text = text;
        _connectButton.Content = connected ? AppStrings.TcpDisconnect : AppStrings.TcpConnect;
    }
}
