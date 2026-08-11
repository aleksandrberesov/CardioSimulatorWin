using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Adds "click outside to dismiss" (light dismiss) to every <see cref="ContentDialog"/>. WinUI's
/// ContentDialog is fully modal with no built-in light-dismiss, so this hooks the dialog template's
/// full-window smoke layer (the <c>LayoutRoot</c> grid that dims the shell behind the card) once the
/// dialog has opened and calls <see cref="ContentDialog.Hide()"/> when the user taps that dimmed area
/// outside the dialog card.
///
/// Dismissing this way resolves the dialog to <see cref="ContentDialogResult.None"/> — exactly the
/// same result as the Close/Cancel button — so an outside tap is always the non-destructive path (no
/// dialog in the app saves or deletes on <c>None</c>).
///
/// Enabled app-wide by the implicit <c>ContentDialog</c> style in <c>App.xaml</c>
/// (<c>ctl:DialogLightDismiss.IsEnabled="True"</c>), so every dialog opts in without a per-call-site
/// change. A dialog that should stay strictly modal can override the style or set
/// <c>DialogLightDismiss.IsEnabled="False"</c> on that instance.
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

        // Toggle the single Opened subscription; the per-open Tapped handler is wired/cleaned in OnOpened.
        dialog.Opened -= OnOpened;
        if (e.NewValue is true)
        {
            dialog.Opened += OnOpened;
        }
    }

    private static void OnOpened(ContentDialog dialog, ContentDialogOpenedEventArgs args)
    {
        // The smoke layer ("LayoutRoot") is the grid that fills the window and dims the shell; the
        // dialog card ("BackgroundElement") is centered inside it. A tap whose OriginalSource is the
        // smoke layer itself landed on the dimmed area outside the card — dismiss. Taps on the card
        // bubble up with a deeper OriginalSource and are ignored.
        if (FindByName(dialog, "LayoutRoot") is not { } smoke)
        {
            return;
        }

        void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, smoke))
            {
                dialog.Hide();
            }
        }

        smoke.Tapped += OnTapped;

        void OnClosed(ContentDialog d, ContentDialogClosedEventArgs e)
        {
            smoke.Tapped -= OnTapped;
            d.Closed -= OnClosed;
        }

        dialog.Closed += OnClosed;
    }

    /// <summary>Depth-first search of the visual tree for a <see cref="FrameworkElement"/> by name.</summary>
    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
            {
                return fe;
            }

            if (FindByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
