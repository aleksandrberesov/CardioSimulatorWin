using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
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
    private readonly RhythmChoosingPanel _panel = new();
    private readonly Border _panelHost;
    private readonly Border _handle;
    private bool _isExpanded;
    private bool _pinned;

    public event EventHandler<PathologyEntry>? RhythmSelected;

    /// <summary>Raised when the in-panel "Fix drawer" checkbox toggles.</summary>
    public event EventHandler<bool>? PinnedChanged;

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
        _panel.PinnedChanged += (_, pinned) => PinnedChanged?.Invoke(this, pinned);

        // Rotated vertical text label, matching the Android SideDrawer handle.
        var label = new TextBlock
        {
            Text = AppStrings.EditorRhythmsTitle,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = -90 },
        };
        _handle = new Border
        {
            Width = 24,
            MinHeight = 96,
            Background = new SolidColorBrush(Colors.Gainsboro),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _handle.Tapped += (_, _) => ToggleExpanded();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        row.Children.Add(_panelHost);
        row.Children.Add(_handle);
        Content = row;
    }

    /// <summary>
    /// Pinned: the panel stays open and the toggle handle is hidden, so the host can lay the
    /// drawer out inline beside the monitor (Android's <c>isDrawerFixed</c> branch). Unpinned
    /// restores the collapsible handle.
    /// </summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        _panel.Pinned = pinned;
        _handle.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        _panelHost.Visibility = pinned || _isExpanded ? Visibility.Visible : Visibility.Collapsed;
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
        if (_pinned) return; // pinned drawer is always open
        _isExpanded = !_isExpanded;
        _panelHost.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
