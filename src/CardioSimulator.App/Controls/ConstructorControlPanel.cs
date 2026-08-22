using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Constructor bottom bar: point navigation (◀ time ▶), ADC adjust (▼ value ▲) and speed (− value +).
/// The arrow/±/▲▼ cells are repeating <see cref="Tab"/>s; the value cells open numeric dialogs.
/// Port of the Android <c>ConstructorControlPanel</c>.
/// </summary>
public sealed class ConstructorControlPanel : UserControl
{
    private readonly ConstructorViewModel _editorVm;
    private readonly MonitorViewModel _monitorVm;

    private readonly Tab _timeTab = new() { MinWidth = 64 };
    private readonly Tab _adcTab = new() { MinWidth = 64 };
    private readonly Tab _algoTab = new() { MinWidth = 80 };
    private readonly Tab _filtersTab = new() { MinWidth = 80, ShowChevron = true };
    private readonly Tab _speedTab = new() { MinWidth = 64 };

    public ConstructorControlPanel(ConstructorViewModel editorVm, MonitorViewModel monitorVm)
    {
        _editorVm = editorVm;
        _monitorVm = monitorVm;
        Content = BuildLayout();
        _editorVm.PropertyChanged += OnVmChanged;
        _monitorVm.PropertyChanged += OnVmChanged;
        Unloaded += (_, _) =>
        {
            _editorVm.PropertyChanged -= OnVmChanged;
            _monitorVm.PropertyChanged -= OnVmChanged;
        };
        Refresh();
    }

    private UIElement BuildLayout()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        var prev = RepeatTab(0xE76B, () => _editorVm.SelectPrevious());
        _timeTab.Click += (_, _) => ShowTimeDialog();
        var next = RepeatTab(0xE76C, () => _editorVm.SelectNext());
        row.Children.Add(Group(prev, _timeTab, next));

        row.Children.Add(Divider());

        var down = RepeatTab(0xE70D, () => _editorVm.MoveSelectedDown());
        _adcTab.Click += (_, _) => ShowAdcDialog();
        var up = RepeatTab(0xE70E, () => _editorVm.MoveSelectedUp());
        row.Children.Add(Group(down, _adcTab, up));

        row.Children.Add(Divider());

        // Editing algorithm + radius (weighted-kernel smoothing) — Android's smoothing dialog.
        _algoTab.Click += (_, _) => ShowSmoothingDialog();
        row.Children.Add(_algoTab);

        row.Children.Add(Divider());

        // Display filter (None / LP / HP / BP) — the same band options as the Teaching monitor,
        // applied to the looping preview and the read-only all-leads overview.
        _filtersTab.Click += (_, _) => ShowFilterFlyout();
        row.Children.Add(_filtersTab);

        row.Children.Add(Divider());

        var minus = RepeatTab(0xE738, () =>
        {
            var s = _monitorVm.MonitorMode.Speed;
            if (s > 1f) _monitorVm.SetSpeed(s - 1f);
        });
        _speedTab.Click += (_, _) => ShowSpeedDialog();
        var plus = RepeatTab(0xE710, () => _monitorVm.SetSpeed(_monitorVm.MonitorMode.Speed + 1f));
        row.Children.Add(Group(minus, _speedTab, plus));

        row.Children.Add(Divider());

        var startStop = new Tab { Glyph = char.ConvertFromUtf32(0xE768), MinWidth = 48 };
        startStop.Click += (_, _) => _monitorVm.SetIsRunning(!_monitorVm.MonitorMode.IsRunning);
        row.Children.Add(startStop);

        return row;
    }

    private static Tab RepeatTab(int glyph, Action onClick)
    {
        var t = new Tab { Glyph = char.ConvertFromUtf32(glyph), IsRepeatable = true, MinWidth = 40 };
        t.Click += (_, _) => onClick();
        return t;
    }

    private static UIElement Group(UIElement a, UIElement b, UIElement c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(a);
        p.Children.Add(b);
        p.Children.Add(c);
        return p;
    }

    private static UIElement Divider() => new Border
    {
        Width = 1,
        Height = 32,
        Background = AppTheme.ControlBorder,
        Margin = new Thickness(4, 0, 4, 0),
    };

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private int[] CurrentSamples()
    {
        var file = _editorVm.TargetFile;
        return file is not null && file.Leads.TryGetValue(_editorVm.FocusedLead, out var s)
            ? s.Samples : Array.Empty<int>();
    }

    private void Refresh()
    {
        var mode = _monitorVm.MonitorMode;
        var samples = CurrentSamples();
        var sel = _editorVm.SelectedIndex;
        var hasSel = sel >= 0 && sel < samples.Length;

        _timeTab.Text = hasSel ? AppStrings.EditorTimeFormat((int)(sel * 1000f / mode.Calibration.SampleRateHz)) : "-";
        _adcTab.Text = hasSel ? AppStrings.EditorAdcFormat(samples[sel].ToString()) : "-";
        _speedTab.Text = mode.Speed % 1 == 0 ? ((int)mode.Speed).ToString() : mode.Speed.ToString("0.#");
        _speedTab.SubText = AppStrings.MonitorSpeedUnit;
        _algoTab.Text = _editorVm.Algorithm.ToString();
        _filtersTab.Text = mode.FilterType switch
        {
            EcgFilterType.None => AppStrings.MonitorFilterNone,
            EcgFilterType.Lowpass => AppStrings.MonitorFilterLp,
            EcgFilterType.Highpass => AppStrings.MonitorFilterHp,
            EcgFilterType.Bandpass => AppStrings.MonitorFilterBp,
            _ => AppStrings.MonitorFilterNone,
        };
    }

    // ── Filters dropdown ────────────────────────────────────────────────────

    /// <summary>Opens the filter chooser, mirroring the Teaching monitor's Filters dropdown (minus the
    /// signal-quality badge, which the constructor has no live monitor to compute).</summary>
    private void ShowFilterFlyout()
    {
        var panel = new StackPanel { MinWidth = 220 };
        panel.Children.Add(BuildFilterHeader());
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = AppTheme.ControlBorder,
            Margin = new Thickness(0, 6, 0, 6),
        });

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.Top, // open upward over the canvas (panel sits at the bottom)
        };

        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameNone, EcgFilterType.None);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameLp, EcgFilterType.Lowpass);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameHp, EcgFilterType.Highpass);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameBp, EcgFilterType.Bandpass);

        flyout.ShowAt(_filtersTab);
    }

    // A flyout title row: bold "Filters" label plus a circled-info sign whose tooltip explains the bands.
    private static UIElement BuildFilterHeader()
    {
        var label = new TextBlock
        {
            Text = AppStrings.MonitorFilters,
            Foreground = AppTheme.TextPrimary,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new FontIcon
        {
            Glyph = char.ConvertFromUtf32(0xE946), // Info (circled "i")
            FontSize = 14,
            Foreground = AppTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(icon, new ToolTip
        {
            Content = new TextBlock { Text = AppStrings.MonitorFiltersInfo, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 },
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 4, 0) };
        row.Children.Add(label);
        row.Children.Add(icon);
        return row;
    }

    // A single-select filter row: a check glyph marks the active filter; selecting applies it and closes.
    private void AddFilterRow(StackPanel panel, Flyout flyout, string text, EcgFilterType filterType)
    {
        var selected = _monitorVm.MonitorMode.FilterType == filterType;
        var glyph = new FontIcon
        {
            Glyph = char.ConvertFromUtf32(0xE73E), // checkmark
            FontSize = 12,
            Foreground = AppTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
        };
        var label = new TextBlock
        {
            Text = text,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var rowContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowContent.Children.Add(glyph);
        rowContent.Children.Add(label);

        var container = new Border
        {
            Child = rowContent,
            Padding = new Thickness(6, 6, 12, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        container.Tapped += (_, _) =>
        {
            _monitorVm.SetFilterType(filterType);
            flyout.Hide();
        };
        container.PointerEntered += (_, _) => container.Background = AppTheme.HoverFill;
        container.PointerExited += (_, _) => container.Background = new SolidColorBrush(Colors.Transparent);

        panel.Children.Add(container);
    }

    private async void ShowSmoothingDialog()
    {
        var algoPanel = new StackPanel { Spacing = 2 };
        algoPanel.Children.Add(new TextBlock { Text = "Algorithm", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var buttons = new List<RadioButton>();
        foreach (var algo in Enum.GetValues<EditingAlgorithm>())
        {
            var rb = new RadioButton { Content = algo.ToString(), GroupName = "algo", Tag = algo, IsChecked = _editorVm.Algorithm == algo };
            buttons.Add(rb);
            algoPanel.Children.Add(rb);
        }

        var radiusBox = new TextBox { Header = "Width (samples)", Text = _editorVm.EditingRadius.ToString() };
        algoPanel.Children.Add(radiusBox);

        var dialog = new ContentDialog
        {
            Title = "Smoothing",
            Content = algoPanel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var chosen = buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as EditingAlgorithm?;
        if (chosen is { } algorithm) _editorVm.SetEditingAlgorithm(algorithm);
        if (int.TryParse(radiusBox.Text, out var radius)) _editorVm.SetEditingRadius(radius);
    }

    private async void ShowTimeDialog()
    {
        var samples = CurrentSamples();
        var sel = _editorVm.SelectedIndex;
        var sampleRate = _monitorVm.MonitorMode.Calibration.SampleRateHz;
        var current = sel >= 0 && sel < samples.Length ? (int)(sel * 1000f / sampleRate) : 0;
        var input = await PromptNumber(AppStrings.EditorSetTimeTitle, AppStrings.EditorTimeUnit, current.ToString());
        if (input is not null && int.TryParse(input, out var ms))
        {
            _editorVm.SelectIndex((int)(ms * sampleRate / 1000f));
        }
    }

    private async void ShowAdcDialog()
    {
        var samples = CurrentSamples();
        var sel = _editorVm.SelectedIndex;
        if (sel < 0 || sel >= samples.Length) return;
        var input = await PromptNumber(AppStrings.EditorSetAdcTitle, AppStrings.EditorAdcLabel, samples[sel].ToString());
        if (input is not null && int.TryParse(input, out var adc))
        {
            _editorVm.SetSample(_editorVm.FocusedLead, sel, adc);
        }
    }

    private async void ShowSpeedDialog()
    {
        var current = _monitorVm.MonitorMode.Speed;
        var initial = current % 1 == 0 ? ((int)current).ToString() : current.ToString("0.#");
        var input = await PromptNumber(AppStrings.MonitorSpeedTitle, AppStrings.MonitorSpeedUnit, initial);
        if (input is not null && float.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            _monitorVm.SetSpeed(speed);
        }
    }

    private async Task<string?> PromptNumber(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Header = label, SelectionStart = initial.Length };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}
