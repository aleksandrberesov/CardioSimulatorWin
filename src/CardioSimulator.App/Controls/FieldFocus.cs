using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Shared focus behaviours for numeric / text entry, so every screen treats a <see cref="NumberBox"/>
/// the same way (originally a Test-Constructor fix, generalised app-wide):
/// <list type="bullet">
///   <item><see cref="SpinButtonsOnlyWhenFocused"/> — the increment/decrement (spin) buttons show only
///   while the field is focused; a bare <c>Compact</c>/<c>Inline</c> NumberBox keeps its spin affordance
///   visible at all times, which reads as "the buttons never go away".</item>
///   <item><see cref="DismissFieldFocusOnEmptyClick"/> — a click on empty (non-interactive) space within
///   a surface drops focus from a focused field, so those spin buttons (and the selection / clear
///   glyph) collapse. WinUI otherwise leaves focus on the field when the click lands on a non-focusable
///   element, so the affordances would linger even after the user clicked away.</item>
/// </list>
/// Use both together: the spin buttons then appear on focus and vanish on blur, including the blur
/// caused by clicking empty space.
/// </summary>
public static class FieldFocus
{
    /// <summary>Shows <paramref name="box"/>'s spin buttons only while it has focus: hidden at rest,
    /// restored on focus, hidden again on blur. The author's chosen shown placement (Inline/Compact) is
    /// preserved; a box left at the default reveals <c>Compact</c> on focus.</summary>
    public static void SpinButtonsOnlyWhenFocused(NumberBox box)
    {
        var shown = box.SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Hidden
            ? NumberBoxSpinButtonPlacementMode.Compact
            : box.SpinButtonPlacementMode;
        box.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        box.GotFocus += (_, _) => box.SpinButtonPlacementMode = shown;
        box.LostFocus += (_, _) => box.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
    }

    /// <summary>
    /// Makes a press on empty (non-interactive) space within <paramref name="surface"/> drop focus from a
    /// focused text/numeric field, so its editing affordances collapse. A hidden, click-through focus
    /// sink is parked focus on (added to the surface's content panel when possible; otherwise the surface
    /// itself is focused). No-op unless a <see cref="TextBox"/> (a plain box or a NumberBox's inner box)
    /// currently owns focus and the press missed every interactive control.
    /// </summary>
    public static void DismissFieldFocusOnEmptyClick(UserControl surface)
    {
        var sink = MakeSink();
        void EnsureSinkParented()
        {
            if (sink.Parent is null && surface.Content is Panel panel) panel.Children.Add(sink);
        }
        surface.Loaded += (_, _) => EnsureSinkParented();
        EnsureSinkParented();
        WireDismiss(surface, surface, sink);
    }

    // A 1×1, transparent, click-through button that can still receive focus (so a field can lose it).
    private static Button MakeSink() => new()
    {
        Width = 1,
        Height = 1,
        Opacity = 0,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    // Attaches the capture-phase press handler to <paramref name="handlerTarget"/>: on a press over empty
    // (non-interactive) space, while a text/numeric field owns focus, parks focus on <paramref name="sink"/>
    // (or, if the sink never got parented, on the target control itself) so the field's affordances collapse.
    private static void WireDismiss(FrameworkElement handlerTarget, FrameworkElement stopAt, Button sink)
    {
        handlerTarget.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var xamlRoot = handlerTarget.XamlRoot;
            if (xamlRoot is null) return;
            // Only act when a text-entry field owns focus (a focused NumberBox reports its inner TextBox).
            if (FocusManager.GetFocusedElement(xamlRoot) is not TextBox) return;
            // Leave focus alone if the press landed on (or inside) an interactive control — the field
            // itself, another field, a button, combo, tab … — so their own click/focus behaves normally.
            if (e.OriginalSource is DependencyObject src && CrossesInteractiveControl(src, stopAt)) return;
            // Empty space: park focus off the field so its spin buttons / selection collapse.
            if (sink.Parent is not null) sink.Focus(FocusState.Pointer);
            else if (handlerTarget is Control control) { control.IsTabStop = true; control.Focus(FocusState.Pointer); }
        }), handledEventsToo: true);
    }

    /// <summary>Walks up from <paramref name="node"/> to <paramref name="stopAt"/>, reporting whether the
    /// press path crosses a focusable interactive control (any enabled tab-stop <see cref="Control"/> — a
    /// field, button, combo, tab …). Plain panels/borders/labels are not Controls, so genuine empty space
    /// returns false.</summary>
    private static bool CrossesInteractiveControl(DependencyObject? node, DependencyObject stopAt)
    {
        while (node is not null && !ReferenceEquals(node, stopAt))
        {
            if (node is Control { IsTabStop: true, IsEnabled: true }) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
