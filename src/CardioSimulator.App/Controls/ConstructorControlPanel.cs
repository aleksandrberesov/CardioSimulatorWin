using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        var minus = RepeatTab(0xE738, () =>
        {
            var s = _monitorVm.MonitorMode.Speed;
            if (s > 1f) _monitorVm.SetSpeed(s - 1f);
        });
        _speedTab.Click += (_, _) => ShowSpeedDialog();
        var plus = RepeatTab(0xE710, () => _monitorVm.SetSpeed(_monitorVm.MonitorMode.Speed + 1f));
        row.Children.Add(Group(minus, _speedTab, plus));

        row.Children.Add(Divider());

        var calc = new Tab { Glyph = char.ConvertFromUtf32(0xE94C), Text = AppStrings.CalcDerivedLeads, MinWidth = 64 };
        calc.Click += (_, _) => _editorVm.CalculateDerivedLeads();
        row.Children.Add(calc);

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
        Background = new SolidColorBrush(Colors.Gray),
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
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}
