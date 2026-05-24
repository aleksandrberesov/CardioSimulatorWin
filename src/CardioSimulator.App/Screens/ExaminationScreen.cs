using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Screens;

/// <summary>Examination mode — empty placeholder stub, matching the Android <c>ExaminationScreen</c>.</summary>
public sealed class ExaminationScreen : UserControl
{
    public ExaminationScreen()
    {
        Content = new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Examination",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Colors.Gray),
                },
            },
        };
    }
}
