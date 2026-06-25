using System.Threading.Tasks;
using CardioSimulator.App.Localization;
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
/// Displacement) in the middle, and the limb-electrode body figure on the right. The state-button
/// behaviour is a scaffold wired in a later increment.
/// </summary>
public static class ElectrodesDialog
{
    private static readonly SolidColorBrush Cream = Hex(0xF2, 0xEF, 0xE6);
    private static readonly SolidColorBrush Blue = Hex(0x5B, 0x9B, 0xD5);
    private static readonly SolidColorBrush BlueHover = Hex(0x4F, 0x8B, 0xC2);
    private static readonly SolidColorBrush BluePressed = Hex(0x42, 0x7A, 0xAE);
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

    public static Task ShowAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Content = BuildContent(),
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = xamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1200d;
        return dialog.ShowAsync().AsTask();
    }

    private static UIElement BuildContent()
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

        // Middle: legend + state buttons.
        var middle = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        middle.Children.Add(LegendRow(Red, AppStrings.ElectrodesRa));
        middle.Children.Add(LegendRow(Yellow, AppStrings.ElectrodesLa));
        middle.Children.Add(LegendRow(Green, AppStrings.ElectrodesRl));
        middle.Children.Add(LegendRow(Black, AppStrings.ElectrodesLl));
        middle.Children.Add(new Border { Height = 8 });
        middle.Children.Add(LegendRow(Red, AppStrings.ElectrodesV1));
        middle.Children.Add(LegendRow(Yellow, AppStrings.ElectrodesV2));
        middle.Children.Add(LegendRow(Green, AppStrings.ElectrodesV3));
        middle.Children.Add(LegendRow(Brown, AppStrings.ElectrodesV4));
        middle.Children.Add(LegendRow(Black, AppStrings.ElectrodesV5));
        middle.Children.Add(LegendRow(Purple, AppStrings.ElectrodesV6));
        middle.Children.Add(new Border { Height = 12 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(StateButton(AppStrings.ElectrodesStateOk));
        buttons.Children.Add(StateButton(AppStrings.ElectrodesStateSwapped));
        buttons.Children.Add(StateButton(AppStrings.ElectrodesStateDisplacement));
        middle.Children.Add(buttons);

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

    private static StackPanel LegendRow(Brush color, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Dot(color));
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

    /// <summary>A blue rounded state button; flat colour across all visual states.</summary>
    private static Button StateButton(string text)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 40,
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
        };
        button.Resources["ButtonBackground"] = Blue;
        button.Resources["ButtonBackgroundPointerOver"] = BlueHover;
        button.Resources["ButtonBackgroundPressed"] = BluePressed;
        button.Resources["ButtonForeground"] = White;
        button.Resources["ButtonForegroundPointerOver"] = White;
        button.Resources["ButtonForegroundPressed"] = White;
        return button;
    }

    private static SolidColorBrush Hex(byte r, byte g, byte b) =>
        new(new Windows.UI.Color { A = 255, R = r, G = g, B = b });
}
