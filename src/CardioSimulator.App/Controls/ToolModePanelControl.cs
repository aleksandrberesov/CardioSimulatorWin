using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Vertical 56 px-wide icon sidebar for switching between <see cref="ToolMode"/>s, plus a separated
/// Tips action at the bottom that opens the annotation-overlay palette (not a mode — an action).
/// Port of the Android <c>ToolModePanel</c>.
/// </summary>
public sealed class ToolModePanelControl : UserControl
{
    private readonly List<(ToolMode Mode, Button Button)> _buttons = new();
    private Button? _tipsButton;

    public event Action<ToolMode>? ModeChanged;

    /// <summary>Raised when the Tips button is clicked (opens the annotation-overlay palette).</summary>
    public event Action? TipsClick;

    private static readonly (ToolMode Mode, string Glyph, string Tip)[] Modes =
    [
        (ToolMode.Select,   "", "Select"),
        (ToolMode.Trace,    "", "Trace"),
        (ToolMode.Position, "", "Position"),
        (ToolMode.Points,   "", "Points"),
        (ToolMode.Photo,    "", "Image"),
        (ToolMode.Pan,      "", "Pan view (drag to move, wheel to zoom)"),
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

        // Tips action — separated from the mode buttons; opens the annotation-overlay palette.
        stack.Children.Add(new Border
        {
            Width = 32,
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = new SolidColorBrush(new Windows.UI.Color { A = 0x40, R = 0x80, G = 0x80, B = 0x80 }),
        });
        var tipsBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Comment),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(tipsBtn, AppStrings.ConstructorTipsButton);
        tipsBtn.Click += (_, _) => TipsClick?.Invoke();
        _tipsButton = tipsBtn;
        stack.Children.Add(tipsBtn);

        return new Border
        {
            Width = 56,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = AppTheme.ControlFill,
            Child = stack,
        };
    }

    public void SetMode(ToolMode mode)
    {
        foreach (var (m, btn) in _buttons)
        {
            btn.Background = m == mode ? AppTheme.AccentTint : null;
        }
        if (_tipsButton is not null)
            _tipsButton.Background = mode == ToolMode.Tips ? AppTheme.AccentTint : null;
    }
}
