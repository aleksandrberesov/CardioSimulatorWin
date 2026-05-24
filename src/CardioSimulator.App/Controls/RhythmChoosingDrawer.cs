using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Collapsible left drawer wrapping a <see cref="RhythmChoosingPanel"/> behind a toggle
/// handle. Port of the Android <c>RhythmChoosingDrawer</c>.
/// </summary>
public sealed class RhythmChoosingDrawer : UserControl
{
    private static readonly string GlyphLeft = char.ConvertFromUtf32(0xE76B);   // ChevronLeft
    private static readonly string GlyphRight = char.ConvertFromUtf32(0xE76C);  // ChevronRight

    private readonly RhythmChoosingPanel _panel = new();
    private readonly Border _panelHost;
    private readonly FontIcon _handleIcon;
    private bool _isExpanded;

    public event EventHandler<PathologyEntry>? RhythmSelected;

    public RhythmChoosingDrawer()
    {
        _panelHost = new Border
        {
            Width = 300,
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            Child = _panel,
            Visibility = Visibility.Collapsed,
        };
        _panel.RhythmSelected += (_, entry) => RhythmSelected?.Invoke(this, entry);

        _handleIcon = new FontIcon
        {
            Glyph = GlyphRight,
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.Black),
        };
        var handle = new Border
        {
            Width = 24,
            Height = 64,
            Background = new SolidColorBrush(Colors.Gainsboro),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = _handleIcon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        handle.Tapped += (_, _) => ToggleExpanded();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        row.Children.Add(_panelHost);
        row.Children.Add(handle);
        Content = row;
    }

    public DomainLanguage DisplayLanguage
    {
        get => _panel.DisplayLanguage;
        set => _panel.DisplayLanguage = value;
    }

    public string? SelectedId
    {
        get => _panel.SelectedId;
        set => _panel.SelectedId = value;
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms) => _panel.SetRhythms(rhythms);

    private void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _panelHost.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        _handleIcon.Glyph = _isExpanded ? GlyphLeft : GlyphRight;
    }
}
