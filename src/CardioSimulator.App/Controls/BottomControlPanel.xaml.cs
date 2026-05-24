using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Bottom bar: a per-mode content slot (e.g. the MonitorControlPanel in Teaching)
/// plus the Settings button. Faithful port of the Android <c>BottomControlPanel</c>.
/// </summary>
public sealed partial class BottomControlPanel : UserControl
{
    public event EventHandler? SettingsClick;

    public BottomControlPanel()
    {
        InitializeComponent();
    }

    /// <summary>The mode-specific content shown on the left (null = empty).</summary>
    public UIElement? PanelContent
    {
        get => ContentHost.Content as UIElement;
        set => ContentHost.Content = value;
    }

    private void OnSettingsClick(object? sender, EventArgs e) => SettingsClick?.Invoke(this, EventArgs.Empty);
}
