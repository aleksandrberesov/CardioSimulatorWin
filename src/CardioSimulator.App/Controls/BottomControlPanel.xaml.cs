using CardioSimulator.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Bottom bar: a per-mode content slot (e.g. the MonitorControlPanel in Teaching)
/// plus the Fullscreen and Settings buttons. Faithful port of the Android <c>BottomControlPanel</c>
/// (the fullscreen tab is a desktop-only addition - Android has no windowed presenter).
/// </summary>
public sealed partial class BottomControlPanel : UserControl
{
    // Segoe Fluent Icons: FullScreen (enter) / BackToWindow (exit).
    private const string GlyphEnterFullScreen = ""; // FullScreen
    private const string GlyphExitFullScreen = "";  // BackToWindow

    public event EventHandler? SettingsClick;
    public event EventHandler? CompareClick;
    public event EventHandler? FullScreenClick;

    public BottomControlPanel()
    {
        InitializeComponent();
        SetFullScreenState(false);
    }

    /// <summary>Reflects the window's fullscreen state on the toggle: swaps the glyph (enter/exit)
    /// and its tooltip. Called by the shell on wire-up and whenever the presenter changes.</summary>
    public void SetFullScreenState(bool isFullScreen)
    {
        FullScreenTab.Glyph = isFullScreen ? GlyphExitFullScreen : GlyphEnterFullScreen;
        ToolTipService.SetToolTip(FullScreenTab,
            isFullScreen ? AppStrings.FullScreenExit : AppStrings.FullScreenEnter);
    }

    /// <summary>The mode-specific content shown on the left (null = empty).</summary>
    public UIElement? PanelContent
    {
        get => ContentHost.Content as UIElement;
        set => ContentHost.Content = value;
    }

    public bool IsCompareVisible
    {
        get => CompareTab.Visibility == Visibility.Visible;
        set
        {
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            CompareTab.Visibility = vis;
            CompareDivider.Visibility = vis;
        }
    }

    private void OnSettingsClick(object? sender, EventArgs e) => SettingsClick?.Invoke(this, EventArgs.Empty);
    private void OnCompareClick(object? sender, EventArgs e) => CompareClick?.Invoke(this, EventArgs.Empty);
    private void OnFullScreenClick(object? sender, EventArgs e) => FullScreenClick?.Invoke(this, EventArgs.Empty);
}
