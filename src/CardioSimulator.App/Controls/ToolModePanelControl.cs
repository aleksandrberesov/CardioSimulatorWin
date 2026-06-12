using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Vertical 56 px-wide icon sidebar for switching between <see cref="ToolMode"/>s.
/// Port of the Android <c>ToolModePanel</c>.
/// </summary>
public sealed class ToolModePanelControl : UserControl
{
    private readonly List<(ToolMode Mode, Button Button)> _buttons = new();

    public event Action<ToolMode>? ModeChanged;

    private static readonly (ToolMode Mode, string Glyph, string Tip)[] Modes =
    [
        (ToolMode.Select,   "", "Select"),
        (ToolMode.Trace,    "", "Trace"),
        (ToolMode.Position, "", "Position"),
        (ToolMode.Points,   "", "Points"),
        (ToolMode.Photo,    "", "Image"),
    ];

    public ToolModePanelControl()
    {
        Content = Build();
    }

    private UIElement Build()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Thickness(4, 12, 4, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        foreach (var (mode, glyph, tip) in Modes)
        {
            var btn = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 18 },
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ToolTipService.SetToolTip(btn, tip);
            var captured = mode;
            btn.Click += (_, _) => ModeChanged?.Invoke(captured);
            _buttons.Add((mode, btn));
            stack.Children.Add(btn);
        }

        return new Border
        {
            Width = 56,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(new Color { A = 0xFF, R = 0xF5, G = 0xF5, B = 0xF8 }),
            Child = stack,
        };
    }

    public void SetMode(ToolMode mode)
    {
        foreach (var (m, btn) in _buttons)
        {
            btn.Background = m == mode
                ? new SolidColorBrush(new Color { A = 0xFF, R = 0xBB, G = 0xD8, B = 0xF5 })
                : null;
        }
    }
}
