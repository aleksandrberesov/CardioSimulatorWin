using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Adds "click outside to dismiss" (light dismiss) to every <see cref="ContentDialog"/>. WinUI's
/// ContentDialog is fully modal with no built-in light-dismiss.
///
/// The dim backdrop is the template's <c>&lt;Rectangle x:Name="SmokeLayerBackground"&gt;</c>. In
/// WindowsAppSDK 1.8 it is NOT under the dialog's own visual subtree — the dialog hosts its card in one
/// popup and the smoke rectangle in a <em>separate</em> popup, so we locate it across the open popups
/// (<see cref="VisualTreeHelper.GetOpenPopupsForXamlRoot"/>). We hook that rectangle's
/// <see cref="UIElement.PointerPressed"/>: since it is drawn behind the centered card and fills the
/// window, any press reaching it is outside the card, so we call <see cref="ContentDialog.Hide()"/>.
/// That resolves the dialog to <see cref="ContentDialogResult.None"/> — the same as the Close/Cancel
/// button — so an outside tap is always the non-destructive path (no dialog in the app saves or deletes
/// on <c>None</c>).
///
/// We hook ONLY the smoke rectangle, never the dialog's own card popup. The smoke never overlaps the
/// card, so an unconditional <c>Hide()</c> on it can never fire for an in-dialog interaction. (Hooking
/// the card popup and trying to tell inside/outside apart by walking the visual tree misfires on the
/// dialog's nested popups — text-box context menus, an Expander, focus visuals — dismissing the dialog
/// mid-interaction.)
///
/// <para><b>Timing race (why the retry loop exists):</b> on the <em>first</em> open the template is
/// realized by the time <c>Opened</c> fires, but on subsequent opens <c>Opened</c> can fire <em>before</em>
/// the template is applied — the smoke rectangle exists but is still unnamed and unmeasured, so a
/// one-shot name lookup finds nothing and light-dismiss silently dies for that (and every later) open.
/// We therefore retry on the dispatcher until the backdrop is found, and <see cref="FindSmokeLayer"/>
/// also accepts the not-yet-named full-window <see cref="Rectangle"/> popup child as a fallback.</para>
///
/// Enabled app-wide by the implicit <c>ContentDialog</c> style in <c>App.xaml</c>
/// (<c>ctl:DialogLightDismiss.IsEnabled="True"</c>), so every dialog opts in without a per-call-site
/// change. Opt a single dialog out with <c>DialogLightDismiss.IsEnabled="False"</c> on that instance.
/// </summary>
public static class DialogLightDismiss
{
    // Upper bound on dispatcher retries while waiting for the dialog template to be realized. The
    // backdrop is normally ready within a tick or two; this is only a runaway guard.
    private const int MaxHookAttempts = 40;

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
        // Per-open state, captured by the retry + cleanup closures below.
        var closed = false;
        UIElement? hookedSmoke = null;
        PointerEventHandler? onPressed = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;

        void OnClosed(ContentDialog d, ContentDialogClosedEventArgs e)
        {
            closed = true;
            timer?.Stop();
            if (hookedSmoke is not null && onPressed is not null)
            {
                hookedSmoke.RemoveHandler(UIElement.PointerPressedEvent, onPressed);
            }

            d.Closed -= OnClosed;
        }

        dialog.Closed += OnClosed;

        // Returns true once hooked (or no longer needed), so the caller can stop the timer.
        bool TryHook()
        {
            if (closed || hookedSmoke is not null)
            {
                return true;
            }

            if (FindSmokeLayer(dialog) is { } smoke)
            {
                // handledEventsToo so we still receive the press even if the modal layer marks it handled.
                onPressed = new PointerEventHandler((_, _) => dialog.Hide());
                smoke.AddHandler(UIElement.PointerPressedEvent, onPressed, handledEventsToo: true);
                hookedSmoke = smoke;
                return true;
            }

            return false;
        }

        // Immediate attempt (covers the fast first-open path). If the template isn't realized yet — on
        // repeat opens Opened fires before it is — poll on a real-time timer until the backdrop appears.
        if (TryHook())
        {
            return;
        }

        if (dialog.DispatcherQueue is { } dq)
        {
            var attempts = 0;
            timer = dq.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(30);
            timer.IsRepeating = true;
            timer.Tick += (t, _) =>
            {
                attempts++;
                if (TryHook() || attempts >= MaxHookAttempts)
                {
                    t.Stop();
                }
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Locates the dialog's dim backdrop. Prefers the named <c>SmokeLayerBackground</c> (its own subtree
    /// first, then every open popup — WindowsAppSDK 1.8 hosts the smoke as a sibling popup's direct
    /// child). Falls back to a popup whose direct <see cref="Rectangle"/> child fills the window: on
    /// repeat opens the backdrop can be present but not yet named, and the fallback only accepts it once
    /// it has been measured to the <see cref="XamlRoot"/> size, so we never hook an unmeasured layer.
    /// </summary>
    private static UIElement? FindSmokeLayer(ContentDialog dialog)
    {
        if (FindByNameSelfOrDescendant(dialog, "SmokeLayerBackground") is { } inDialog)
        {
            return inDialog;
        }

        try
        {
            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot);

            foreach (var popup in popups)
            {
                if (popup.Child is { } child && FindByNameSelfOrDescendant(child, "SmokeLayerBackground") is { } found)
                {
                    return found;
                }
            }

            // Fallback: the smoke popup's direct child is a full-window Rectangle. Require it to be
            // measured to (roughly) the window size so we don't hook it before layout gives it hit-test
            // bounds — otherwise the press would sail straight through.
            var rootWidth = dialog.XamlRoot?.Size.Width ?? 0;
            if (rootWidth > 0)
            {
                foreach (var popup in popups)
                {
                    if (popup.Child is Rectangle rect && rect.ActualWidth >= rootWidth - 1)
                    {
                        return rect;
                    }
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
