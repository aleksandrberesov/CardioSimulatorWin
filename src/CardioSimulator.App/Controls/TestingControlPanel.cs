using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Testing-mode top sub-panel: a blank tab, a question label, and a pause button.
/// Port of the Android <c>TestingControlPanel</c> (placeholder content).
/// </summary>
public sealed class TestingControlPanel : UserControl
{
    private static readonly string GlyphPause = char.ConvertFromUtf32(0xE769);

    public TestingControlPanel()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new Tab { Text = "                  ", HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(new TextBlock
        {
            Text = "Question 13",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.Red),
            FontSize = 24,
            VerticalAlignment = VerticalAlignment.Center,
        });
        right.Children.Add(new Tab { Glyph = GlyphPause });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        Content = grid;
    }
}
