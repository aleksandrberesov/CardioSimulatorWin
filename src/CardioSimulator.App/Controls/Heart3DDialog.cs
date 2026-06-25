using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CardioSimulator.App.Controls;

/// <summary>
/// "3D" heart window opened from the monitor control panel. A modal pop-over
/// (<see cref="ContentDialog"/>, so it floats above the native Win2D monitor surface). Lays out the
/// three panels from the design: a left column of function buttons, a middle description panel
/// ("what is happening / a 12-lead ECG window"), and the heart/leads image with an "ECG lead"
/// button on the right. Currently a scaffold — the buttons and the rotatable 3D viewport are wired
/// in a later increment.
/// </summary>
public static class Heart3DDialog
{
    private static readonly SolidColorBrush Cream = Hex(0xF2, 0xEF, 0xE6);
    private static readonly SolidColorBrush Blue = Hex(0x5B, 0x9B, 0xD5);
    private static readonly SolidColorBrush BlueHover = Hex(0x4F, 0x8B, 0xC2);
    private static readonly SolidColorBrush BluePressed = Hex(0x42, 0x7A, 0xAE);
    private static readonly SolidColorBrush White = Hex(0xFF, 0xFF, 0xFF);

    public static Task ShowAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.Monitor3DTitle,
            Content = BuildContent(),
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = xamlRoot,
        };
        // The three-panel layout is wider than the default dialog cap.
        dialog.Resources["ContentDialogMaxWidth"] = 1200d;
        return dialog.ShowAsync().AsTask();
    }

    private static UIElement BuildContent()
    {
        var grid = new Grid
        {
            Background = Cream,
            Padding = new Thickness(16),
            ColumnSpacing = 16,
            Width = 940,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left column: function buttons.
        var left = new StackPanel { Spacing = 10, Width = 190, VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(FunctionButton(AppStrings.Monitor3DLeadScheme));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(2)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(3)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DMi));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(5)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(6)));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Middle column: description / 12-lead ECG panel.
        var middleText = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        middleText.Children.Add(PanelText(AppStrings.Monitor3DDescription));
        middleText.Children.Add(PanelText(AppStrings.Monitor3DOrEcg));
        var middle = new Border
        {
            Background = Blue,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            MinWidth = 240,
            MinHeight = 360,
            Child = middleText,
        };
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        // Right column: heart/leads image + "ECG lead" button.
        var right = new StackPanel { Spacing = 12, Width = 320, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(new Border
        {
            Background = White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new Image
            {
                Source = new BitmapImage(new System.Uri("ms-appx:///Assets/heart_3d.png")),
                Stretch = Stretch.Uniform,
                Height = 300,
            },
        });
        right.Children.Add(FunctionButton(AppStrings.Monitor3DEcgLead));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>A blue rounded button matching the design; flat color across all visual states.</summary>
    private static Button FunctionButton(string text)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
        };
        // Override the themed accent/hover brushes so the button stays the design blue throughout.
        button.Resources["ButtonBackground"] = Blue;
        button.Resources["ButtonBackgroundPointerOver"] = BlueHover;
        button.Resources["ButtonBackgroundPressed"] = BluePressed;
        button.Resources["ButtonForeground"] = White;
        button.Resources["ButtonForegroundPointerOver"] = White;
        button.Resources["ButtonForegroundPressed"] = White;
        return button;
    }

    private static TextBlock PanelText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = White,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static SolidColorBrush Hex(byte r, byte g, byte b) =>
        new(new Windows.UI.Color { A = 255, R = r, G = g, B = b });
}
