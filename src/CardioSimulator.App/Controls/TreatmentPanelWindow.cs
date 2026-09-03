using System;
using CardioSimulator.App.Screens;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CardioSimulator.App.Controls;

/// <summary>
/// «Лечение» — the treatment / resuscitation panel docked to the right edge of the Teaching monitor as a
/// <see cref="Popup"/> (so it composites above the native Win2D surface). It hosts a <see cref="TreatmentPanel"/>
/// bound to the SHARED rhythm view-model and seeded from the currently-displayed rhythm — it is a part of
/// Teaching, not a mode. Toggled by the monitor bottom-bar Treatment button; mirrors <see cref="EosWindow"/>.
/// </summary>
public static class TreatmentPanelWindow
{
    private const double PanelWidth = 500; // wide enough for the two-column card layout

    private static Popup? _popup;
    private static TreatmentPanel? _panel;
    private static Action? _onClosed;

    /// <summary>True while the panel is showing.</summary>
    public static bool IsOpen => _popup is { IsOpen: true };

    /// <summary>
    /// Opens the treatment panel on the right of the monitor (or closes it if already open), seeded from the
    /// currently-displayed rhythm.
    /// </summary>
    /// <param name="onClosed">Invoked whenever the panel closes — including via its own ✕ button — so the host
    /// can un-highlight the Treatment button.</param>
    /// <returns><c>true</c> if the panel is now open, <c>false</c> if this toggle closed it.</returns>
    public static bool Toggle(XamlRoot xamlRoot, RhythmViewModel rhythmVm, AppViewModel appVm, Action? onClosed = null)
    {
        if (_popup is { IsOpen: true })
        {
            Close();
            return false;
        }
        Open(xamlRoot, rhythmVm, appVm, onClosed);
        return true;
    }

    /// <summary>Closes the panel if open — stopping its pending-effect timer so a queued Tick can't mutate the
    /// shared rhythm view-model after close — then fires the close callback registered at open time.</summary>
    public static void Close()
    {
        _panel?.Teardown();
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
        _panel = null;
        var cb = _onClosed;
        _onClosed = null;
        cb?.Invoke();
    }

    private static void Open(XamlRoot xamlRoot, RhythmViewModel rhythmVm, AppViewModel appVm, Action? onClosed)
    {
        _onClosed = onClosed;
        const double topMargin = 72;    // clears the top mode bar
        const double bottomMargin = 72; // clears the bottom control panel
        const double rightMargin = 16;
        var size = xamlRoot.Size;
        var maxHeight = Math.Max(260, size.Height - topMargin - bottomMargin);

        _panel = new TreatmentPanel();
        _panel.Initialize(new TreatmentViewModel(), rhythmVm, appVm, Close);

        // Opaque card behind the dense panel (unlike the translucent EOS reference window), docked right so the
        // live monitor stays visible to its left. The card SIZES TO the panel's content (capped at the
        // available height) instead of stretching full-height, so there is no empty space below the controls;
        // the inner ScrollViewer engages only if the content ever exceeds the cap (very short windows).
        var host = new Border
        {
            Width = PanelWidth,
            MaxHeight = maxHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = new ScrollViewer
            {
                Content = _panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };

        _popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = host,
            HorizontalOffset = Math.Max(0, size.Width - PanelWidth - rightMargin),
            VerticalOffset = topMargin,
            IsLightDismissEnabled = false,
        };
        _popup.IsOpen = true;
    }
}
