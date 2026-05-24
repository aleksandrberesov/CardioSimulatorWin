using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Screens;

/// <summary>OSKE mode — empty placeholder stub, matching the Android <c>OSKEScreen</c>.</summary>
public sealed class OSKEScreen : UserControl
{
    public OSKEScreen()
    {
        Content = new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "OSKE",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Colors.Gray),
                },
            },
        };
    }
}
