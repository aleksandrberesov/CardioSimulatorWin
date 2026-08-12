using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Adds "click outside to dismiss" (light dismiss) to every <see cref="ContentDialog"/>. WinUI's
/// ContentDialog is fully modal with no built-in light-dismiss.
///
/// The dim backdrop is the template's <c>&lt;Rectangle x:Name="SmokeLayerBackground"&gt;</c>. In
/// WindowsAppSDK 1.8 it is NOT under the dialog's own visual subtree — the dialog hosts its card in one
/// popup and the smoke rectangle in a <em>separate</em> popup, so we locate it across the open popups
/// (<see cref="VisualTreeHelper.GetOpenPopupsForXamlRoot"/>), testing each node itself, not just its
/// descendants. On <c>Opened</c> we hook that rectangle's <see cref="UIElement.PointerPressed"/>: since
/// the rectangle is drawn behind the centered card and fills the window, any press that reaches it is
/// outside the card, so we call <see cref="ContentDialog.Hide()"/>. That resolves the dialog to
/// <see cref="ContentDialogResult.None"/> — the same as the Close/Cancel button — so an outside tap is
/// always the non-destructive path (no dialog in the app saves or deletes on <c>None</c>).
///
/// Enabled app-wide by the implicit <c>ContentDialog</c> style in <c>App.xaml</c>
/// (<c>ctl:DialogLightDismiss.IsEnabled="True"</c>), so every dialog opts in without a per-call-site
/// change. Opt a single dialog out with <c>DialogLightDismiss.IsEnabled="False"</c> on that instance.
/// </summary>
public static class DialogLightDismiss
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DialogLightDismiss),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentDialog dialog)
        {
            return;
        }

        dialog.Opened -= OnOpened;
        if (e.NewValue is true)
        {
            dialog.Opened += OnOpened;
        }
    }

    private static void OnOpened(ContentDialog dialog, ContentDialogOpenedEventArgs args)
    {
        if (FindSmokeLayer(dialog) is not { } smoke)
        {
            return;
        }

        // handledEventsToo so we still receive the press even if the modal layer marks it handled.
        var onPressed = new PointerEventHandler((_, _) => dialog.Hide());
        smoke.AddHandler(UIElement.PointerPressedEvent, onPressed, handledEventsToo: true);

        void OnClosed(ContentDialog d, ContentDialogClosedEventArgs e)
        {
            smoke.RemoveHandler(UIElement.PointerPressedEvent, onPressed);
            d.Closed -= OnClosed;
        }

        dialog.Closed += OnClosed;
    }

    /// <summary>
    /// Locates the dialog's dim backdrop (<c>SmokeLayerBackground</c>). Checks the dialog's own subtree
    /// first (older templates), then every open popup's child subtree — WindowsAppSDK 1.8 hosts the
    /// smoke rectangle as a sibling popup's direct child, so the search must test each node itself, not
    /// just its descendants.
    /// </summary>
    private static UIElement? FindSmokeLayer(ContentDialog dialog)
    {
        if (FindByNameSelfOrDescendant(dialog, "SmokeLayerBackground") is { } inDialog)
        {
            return inDialog;
        }

        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot))
            {
                if (popup.Child is { } child && FindByNameSelfOrDescendant(child, "SmokeLayerBackground") is { } found)
                {
                    return found;
                }
            }
        }
        catch
        {
            // XamlRoot may be unavailable in edge cases — light dismiss is best-effort.
        }

        return null;
    }

    private static UIElement? FindByNameSelfOrDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement { } element && element.Name == name)
        {
            return element;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindByNameSelfOrDescendant(VisualTreeHelper.GetChild(root, i), name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
