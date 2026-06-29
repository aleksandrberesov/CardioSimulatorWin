using System.Collections.Generic;
using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace CardioSimulator.App.Controls;

/// <summary>
/// "Электроды" (Electrodes) window opened from the monitor control panel. A modal pop-over
/// (<see cref="ContentDialog"/>, so it floats above the native Win2D monitor surface) laying out the
/// standard-lead reference from the design: a title bar, the chest-placement / cross-section images
/// on the left, the colour-coded electrode legend with the state buttons (All OK / Swapped /
/// Displacement) in the middle, and the limb-electrode body figure on the right.
///
/// The three state buttons are a mutually-exclusive segmented control that drives a real
/// electrode-hookup fault on the live monitor via <see cref="MonitorViewModel.SetElectrodeState"/>:
/// "Swapped" applies the RA/LA limb-electrode reversal and "Displacement" attenuates the precordial
/// leads (see <see cref="ElectrodeFault"/>). The window also reflects the selection visually —
/// the RA/LA legend dots swap colour, the V-lead group dims, and a caption explains the effect.
/// The fault stays applied after the window closes so the student can study the distorted trace.
/// </summary>
public static class ElectrodesDialog
{
    private static readonly SolidColorBrush Cream = Hex(0xF2, 0xEF, 0xE6);
    private static readonly SolidColorBrush Blue = Hex(0x5B, 0x9B, 0xD5);
    private static readonly SolidColorBrush BlueFaint = new(new Windows.UI.Color { A = 36, R = 0x5B, G = 0x9B, B = 0xD5 });
    private static readonly SolidColorBrush White = Hex(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush Ink = Hex(0x22, 0x22, 0x22);
    private static readonly SolidColorBrush DotBorder = Hex(0x88, 0x88, 0x88);

    // Electrode colours (legend dots).
    private static readonly SolidColorBrush Red = Hex(0xE5, 0x39, 0x35);
    private static readonly SolidColorBrush Yellow = Hex(0xFD, 0xD8, 0x35);
    private static readonly SolidColorBrush Green = Hex(0x43, 0xA0, 0x47);
    private static readonly SolidColorBrush Black = Hex(0x10, 0x10, 0x10);
    private static readonly SolidColorBrush Brown = Hex(0x8D, 0x6E, 0x63);
    private static readonly SolidColorBrush Purple = Hex(0x8E, 0x24, 0xAA);

    public static Task ShowAsync(XamlRoot xamlRoot, MonitorViewModel monitorVm)
    {
        var dialog = new ContentDialog
        {
            Content = BuildContent(monitorVm),
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = xamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1200d;
        return dialog.ShowAsync().AsTask();
    }

    private static UIElement BuildContent(MonitorViewModel monitorVm)
    {
        var root = new StackPanel { Background = Cream, Padding = new Thickness(16), Spacing = 12, Width = 960 };

        // Title bar (blue pill, right-aligned).
        root.Children.Add(new Border
        {
            Background = Blue,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = AppStrings.ElectrodesSystemStandard,
                Foreground = White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
            },
        });

        var body = new Grid { ColumnSpacing = 20 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: chest placement + cross-section anatomy images.
        var left = new StackPanel { Spacing = 12, Width = 240, VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(ImageCard("electrodes_chest.png", AppStrings.ElectrodesCaptionChest, 180));
        left.Children.Add(ImageCard("electrodes_cross.png", AppStrings.ElectrodesCaptionCross, 150));
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        // Middle: legend + state buttons. The RA/LA dots are kept as references so "Swapped" can
        // recolour them; the precordial rows are grouped so "Displacement" can dim them.
        var middle = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 340 };
        var raDot = Dot(Red);
        var laDot = Dot(Yellow);
        middle.Children.Add(LegendRow(raDot, AppStrings.ElectrodesRa));
        middle.Children.Add(LegendRow(laDot, AppStrings.ElectrodesLa));
        middle.Children.Add(LegendRow(Dot(Green), AppStrings.ElectrodesRl));
        middle.Children.Add(LegendRow(Dot(Black), AppStrings.ElectrodesLl));
        middle.Children.Add(new Border { Height = 8 });

        var vGroup = new StackPanel { Spacing = 6 };
        vGroup.Children.Add(LegendRow(Dot(Red), AppStrings.ElectrodesV1));
        vGroup.Children.Add(LegendRow(Dot(Yellow), AppStrings.ElectrodesV2));
        vGroup.Children.Add(LegendRow(Dot(Green), AppStrings.ElectrodesV3));
        vGroup.Children.Add(LegendRow(Dot(Brown), AppStrings.ElectrodesV4));
        vGroup.Children.Add(LegendRow(Dot(Black), AppStrings.ElectrodesV5));
        vGroup.Children.Add(LegendRow(Dot(Purple), AppStrings.ElectrodesV6));
        middle.Children.Add(vGroup);
        middle.Children.Add(new Border { Height = 12 });

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        // Built in explicit left-to-right order (Ok | Swapped | Displacement); the dictionary is only
        // used for per-state lookup in Select().
        var ordered = new[]
        {
            (ElectrodeState.Ok, StateButton(AppStrings.ElectrodesStateOk)),
            (ElectrodeState.Swapped, StateButton(AppStrings.ElectrodesStateSwapped)),
            (ElectrodeState.Displacement, StateButton(AppStrings.ElectrodesStateDisplacement)),
        };
        var buttons = new Dictionary<ElectrodeState, (Border border, TextBlock label)>();
        foreach (var (state, entry) in ordered)
        {
            buttons[state] = entry;
            buttonRow.Children.Add(entry.border);
        }
        middle.Children.Add(buttonRow);

        var caption = new TextBlock
        {
            Foreground = Ink,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        middle.Children.Add(caption);

        // Current selection (captured by the hover handlers so only inactive buttons wash on hover).
        var selected = monitorVm.MonitorMode.ElectrodeState;

        void Select(ElectrodeState state, bool pushToModel)
        {
            selected = state;
            foreach (var (s, entry) in buttons) ApplyButtonState(entry.border, entry.label, s == state);

            var swapped = state == ElectrodeState.Swapped;
            raDot.Fill = swapped ? Yellow : Red;
            laDot.Fill = swapped ? Red : Yellow;
            vGroup.Opacity = state == ElectrodeState.Displacement ? 0.45 : 1.0;
            caption.Text = state switch
            {
                ElectrodeState.Swapped => AppStrings.ElectrodesStateCaptionSwapped,
                ElectrodeState.Displacement => AppStrings.ElectrodesStateCaptionDisplacement,
                _ => AppStrings.ElectrodesStateCaptionOk,
            };

            if (pushToModel) monitorVm.SetElectrodeState(state);
        }

        foreach (var (state, entry) in buttons)
        {
            var captured = state;
            entry.border.Tapped += (_, _) => Select(captured, pushToModel: true);
            entry.border.PointerEntered += (_, _) =>
            {
                if (selected != captured) entry.border.Background = BlueFaint;
            };
            entry.border.PointerExited += (_, _) =>
            {
                if (selected != captured) entry.border.Background = White;
            };
        }

        // Reflect the live state when the window opens (no model write-back).
        Select(selected, pushToModel: false);

        Grid.SetColumn(middle, 1);
        body.Children.Add(middle);

        // Right: limb-electrode body figure.
        var right = new Border
        {
            Width = 240,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Picture("electrodes_body.png", 330),
        };
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        root.Children.Add(body);
        return root;
    }

    private static StackPanel LegendRow(Ellipse dot, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(dot);
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Ink,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    private static Ellipse Dot(Brush color) => new()
    {
        Width = 14,
        Height = 14,
        Fill = color,
        Stroke = DotBorder,
        StrokeThickness = 0.5,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>An anatomy image in a white card with a caption beneath it.</summary>
    private static StackPanel ImageCard(string asset, string caption, double height)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new Border
        {
            Background = White,
            BorderBrush = DotBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = Picture(asset, height),
        });
        stack.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Ink,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return stack;
    }

    private static Image Picture(string asset, double height) => new()
    {
        Source = new BitmapImage(new System.Uri($"ms-appx:///Assets/{asset}")),
        Stretch = Stretch.Uniform,
        Height = height,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>A rounded segmented-control cell (Border + centred label), styled by
    /// <see cref="ApplyButtonState"/>. A Border (not a Button) so the active/inactive fill is fully
    /// controlled, matching the toggle idiom used by the monitor control panel.</summary>
    private static (Border border, TextBlock label) StateButton(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            Child = label,
            MinHeight = 40,
            MinWidth = 110,
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Blue,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        return (border, label);
    }

    /// <summary>Active = solid blue fill + white text; inactive = white fill + blue outline/text.</summary>
    private static void ApplyButtonState(Border border, TextBlock label, bool active)
    {
        border.Background = active ? Blue : White;
        border.BorderBrush = Blue;
        label.Foreground = active ? White : Blue;
    }

    private static SolidColorBrush Hex(byte r, byte g, byte b) =>
        new(new Windows.UI.Color { A = 255, R = r, G = g, B = b });
}
