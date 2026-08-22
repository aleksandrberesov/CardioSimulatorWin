using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Adds "click/tap outside to dismiss" (light dismiss) to every <see cref="ContentDialog"/>. WinUI's
/// ContentDialog is fully modal with no built-in light-dismiss.
///
/// <para>The dialog renders as two layers: a dim backdrop that fills the window
/// (<c>&lt;Rectangle x:Name="SmokeLayerBackground"&gt;</c>) and the centered content card
/// (<c>&lt;Border x:Name="BackgroundElement"&gt;</c>). In WindowsAppSDK&#160;1.8 these live in two
/// <em>separate</em> popups (the card in one, the smoke in another), so we enumerate every open popup for
/// the dialog's <see cref="XamlRoot"/> via <see cref="VisualTreeHelper.GetOpenPopupsForXamlRoot"/>.</para>
///
/// <para>On <c>Opened</c> we hook both <see cref="UIElement.PointerPressed"/> and
/// <see cref="UIElement.Tapped"/> on each popup's root element, with <c>handledEventsToo</c> so we still
/// see input the modal layer marks handled. A press/tap dismisses the dialog <em>unless</em> its
/// <see cref="RoutedEventArgs.OriginalSource"/> lies within the content card — so every interaction with
/// the dialog itself (buttons, inputs, scrollbars, empty card space) is preserved while any press on the
/// backdrop closes it. Handling <b>both</b> pointer and tap events, on <b>every</b> popup root rather than
/// only the named smoke rectangle, keeps dismissal working across mouse, touch and pen, and across
/// template/SDK changes. If the card can't be located we fall back to hooking the smoke rectangle alone
/// (unconditional Hide), still safe because the smoke never overlaps the card.</para>
///
/// <para><see cref="ContentDialog.Hide()"/> resolves the dialog to <see cref="ContentDialogResult.None"/>
/// — identical to the Close/Cancel button — so an outside tap is always the non-destructive path (no
/// dialog in the app saves or deletes on <c>None</c>).</para>
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
        var xamlRoot = dialog.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        // The content card. A press whose OriginalSource is inside it is an interaction with the dialog
        // and must NOT dismiss; anything else is a press on the backdrop and dismisses.
        var card = FindAcrossPopups(xamlRoot, "BackgroundElement");

        // Robust path: hook every open popup root (the card popup AND the smoke popup), so an outside
        // press is caught whichever layer hit-tests it — mouse, touch or pen. The card guard keeps
        // in-dialog interaction working. This is only safe when we have the card to guard against, so a
        // missing card falls back to the backdrop-only path below.
        var targets = new List<UIElement>();
        if (card is not null)
        {
            try
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                {
                    if (popup.Child is UIElement root)
                    {
                        targets.Add(root);
                    }
                }
            }
            catch
            {
                // XamlRoot may be unavailable in edge cases — light dismiss is best-effort.
            }
        }

        // Fallback: no card reference (or no popups) — hook the dim backdrop alone with an unconditional
        // Hide. Safe because the smoke rectangle never overlaps the card, so it only ever receives
        // outside presses. This is the original, field-proven behaviour.
        if (targets.Count == 0)
        {
            card = null;
            if (FindAcrossPopups(xamlRoot, "SmokeLayerBackground") is { } smoke)
            {
                targets.Add(smoke);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        void Dismiss(DependencyObject? originalSource)
        {
            // Dismiss only when the press is provably outside the card (or we have no card to guard).
            if (card is null || !IsWithin(originalSource, card))
            {
                dialog.Hide();
            }
        }

        var registrations = new List<(UIElement Element, PointerEventHandler Press, TappedEventHandler Tap)>();
        foreach (var target in targets)
        {
            var press = new PointerEventHandler((_, e) => Dismiss(e.OriginalSource as DependencyObject));
            var tap = new TappedEventHandler((_, e) => Dismiss(e.OriginalSource as DependencyObject));
            target.AddHandler(UIElement.PointerPressedEvent, press, handledEventsToo: true);
            target.AddHandler(UIElement.TappedEvent, tap, handledEventsToo: true);
            registrations.Add((target, press, tap));
        }

        void OnClosed(ContentDialog d, ContentDialogClosedEventArgs e)
        {
            foreach (var (element, press, tap) in registrations)
            {
                element.RemoveHandler(UIElement.PointerPressedEvent, press);
                element.RemoveHandler(UIElement.TappedEvent, tap);
            }

            d.Closed -= OnClosed;
        }

        dialog.Closed += OnClosed;
    }

    /// <summary>Walks the visual tree up from <paramref name="node"/>, returning true if it reaches
    /// <paramref name="ancestor"/> (i.e. the node is the ancestor or a descendant of it).</summary>
    private static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// Finds a named element across every open popup for <paramref name="xamlRoot"/>. WindowsAppSDK 1.8
    /// hosts the dialog card and the smoke rectangle in sibling popups, so the search tests each popup
    /// child node itself as well as its descendants.
    /// </summary>
    private static UIElement? FindAcrossPopups(XamlRoot xamlRoot, string name)
    {
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is { } child && FindByNameSelfOrDescendant(child, name) is { } found)
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
        if (root is FrameworkElement element && element.Name == name)
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
