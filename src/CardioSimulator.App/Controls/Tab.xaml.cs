using CardioSimulator.App.Theming;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Bordered, rounded, clickable cell mirroring the Android <c>Tab</c> composable.
/// Shows an icon <see cref="Glyph"/>, or <see cref="Text"/> with optional stacked
/// <see cref="SubText"/>. <see cref="ShowChevron"/> turns it into a dropdown-style pill;
/// <see cref="IsActive"/> fills it with the accent (toggled/selected state). When
/// <see cref="IsRepeatable"/> is set, holding it fires <see cref="Click"/> repeatedly with
/// acceleration (Android <c>repeatingClickable</c>: immediate, then a 600 ms delay, then
/// ~200 ms accelerating to a 50 ms floor).
/// </summary>
public sealed partial class Tab : UserControl
{
    public event EventHandler? Click;

    private static readonly Thickness ZeroThickness = new(0);
    private static readonly Thickness HairThickness = new(1);
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private DispatcherQueueTimer? _repeatTimer;
    private long _pressStartMs;
    private double _currentDelayMs;
    private bool _pressed;
    private bool _hovered;

    public Tab()
    {
        InitializeComponent();
        UpdateContent();
        ApplySizing();
        ApplyVisualState();
        RootBorder.PointerPressed += OnPointerPressed;
        RootBorder.PointerReleased += OnPointerUp;
        RootBorder.PointerExited += OnPointerExited;
        RootBorder.PointerCanceled += OnPointerUp;
        RootBorder.PointerCaptureLost += OnPointerUp;
        RootBorder.PointerEntered += OnPointerEntered;
    }

    /// <summary>When true, holding fires <see cref="Click"/> repeatedly (accelerating).</summary>
    public bool IsRepeatable { get; set; }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Tab), new PropertyMetadata(null, OnVisualChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(Tab), new PropertyMetadata(null, OnVisualChanged));

    public string? SubText
    {
        get => (string?)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Tab), new PropertyMetadata(null, OnVisualChanged));

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Selected/toggled state: fills with the accent and inverts the foreground.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Tab), new PropertyMetadata(false, OnStateChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Optional override for the fill used while <see cref="IsActive"/> (defaults to the app
    /// accent). Lets a single tab signal a non-default active colour — e.g. a red "fault" highlight on
    /// the Electrodes tab — without changing the shared accent.</summary>
    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(Tab), new PropertyMetadata(null, OnStateChanged));

    public Brush? ActiveBrush
    {
        get => (Brush?)GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    /// <summary>Shows a trailing chevron and the light "dropdown pill" resting look.</summary>
    public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.Register(
        nameof(ShowChevron), typeof(bool), typeof(Tab), new PropertyMetadata(false, OnChevronChanged));

    public bool ShowChevron
    {
        get => (bool)GetValue(ShowChevronProperty);
        set => SetValue(ShowChevronProperty, value);
    }

    // ── Resting pill metrics ────────────────────────────────────────────────
    // Dense sizing is the default used by the packed monitor/bottom panels. The Large variant
    // matches the top-bar selector proportions in the new design: a taller pill with roomier
    // padding and a larger label/chevron, so the 8px corner reads as a rounded-rect (not a capsule).
    private static readonly Thickness DensePadding = new(9, 3, 9, 3);
    private static readonly Thickness LargePadding = new(14, 7, 14, 7);

    /// <summary>Top-bar selector sizing: taller pill, roomier padding and a larger label/chevron.</summary>
    public static readonly DependencyProperty LargeProperty = DependencyProperty.Register(
        nameof(Large), typeof(bool), typeof(Tab), new PropertyMetadata(false, OnSizeChanged));

    public bool Large
    {
        get => (bool)GetValue(LargeProperty);
        set => SetValue(LargeProperty, value);
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Tab)d).ApplySizing();

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Tab)d).UpdateContent();

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Tab)d).ApplyVisualState();

    private static void OnChevronChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tab = (Tab)d;
        tab.ChevronView.Visibility = tab.ShowChevron ? Visibility.Visible : Visibility.Collapsed;
        tab.ApplyVisualState();
    }

    private void UpdateContent()
    {
        var hasGlyph = !string.IsNullOrEmpty(Glyph);
        IconView.Visibility = hasGlyph ? Visibility.Visible : Visibility.Collapsed;
        TextStack.Visibility = hasGlyph ? Visibility.Collapsed : Visibility.Visible;
        if (hasGlyph) IconView.Glyph = Glyph;

        TextView.Text = Text ?? string.Empty;
        var hasSub = !string.IsNullOrEmpty(SubText);
        SubTextView.Visibility = hasSub ? Visibility.Visible : Visibility.Collapsed;
        SubTextView.Text = SubText ?? string.Empty;
    }

    /// <summary>Applies the resting pill metrics (padding + font sizes) for the current size variant.</summary>
    private void ApplySizing()
    {
        RootBorder.Padding = Large ? LargePadding : DensePadding;
        TextView.FontSize = Large ? 14 : 13;
        SubTextView.FontSize = Large ? 10 : 9;
        ChevronView.FontSize = Large ? 10 : 9;
        IconView.FontSize = Large ? 17 : 16;
    }

    /// <summary>Resolves background, border and foreground from the active/hover/chevron state.</summary>
    private void ApplyVisualState()
    {
        var fg = IsActive ? AppTheme.OnAccent : AppTheme.TextPrimary;
        var subFg = IsActive ? AppTheme.OnAccent : AppTheme.TextSecondary;
        IconView.Foreground = fg;
        TextView.Foreground = fg;
        SubTextView.Foreground = subFg;
        ChevronView.Foreground = subFg;

        if (IsActive)
        {
            var fill = ActiveBrush ?? AppTheme.Accent;
            RootBorder.Background = fill;
            RootBorder.BorderBrush = fill;
            RootBorder.BorderThickness = ZeroThickness;
        }
        else if (ShowChevron)
        {
            // White dropdown pill with a hairline border; a light grey wash on hover.
            RootBorder.Background = _hovered || _pressed ? AppTheme.ControlFill : AppTheme.PanelBackground;
            RootBorder.BorderBrush = AppTheme.ControlBorder;
            RootBorder.BorderThickness = HairThickness;
        }
        else if (_hovered || _pressed)
        {
            RootBorder.Background = AppTheme.HoverFill;
            RootBorder.BorderBrush = TransparentBrush;
            RootBorder.BorderThickness = ZeroThickness;
        }
        else
        {
            RootBorder.Background = TransparentBrush;
            RootBorder.BorderBrush = TransparentBrush;
            RootBorder.BorderThickness = ZeroThickness;
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsRepeatable) return; // repeatable taps are driven by the pointer-press loop
        Click?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsRepeatable) return;
        _pressed = true;
        ApplyVisualState();
        RootBorder.CapturePointer(e.Pointer);
        _pressStartMs = Environment.TickCount64;
        _currentDelayMs = 200;
        Click?.Invoke(this, EventArgs.Empty); // immediate fire
        EnsureTimer();
        _repeatTimer!.Interval = TimeSpan.FromMilliseconds(600); // initial hold delay
        _repeatTimer.Start();
    }

    private void EnsureTimer()
    {
        if (_repeatTimer is not null) return;
        _repeatTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _repeatTimer.IsRepeating = true;
        _repeatTimer.Tick += OnRepeatTick;
    }

    private void OnRepeatTick(DispatcherQueueTimer sender, object args)
    {
        if (!_pressed) { sender.Stop(); return; }
        Click?.Invoke(this, EventArgs.Empty);
        var elapsed = Environment.TickCount64 - _pressStartMs;
        if (elapsed >= 1000) _currentDelayMs = Math.Max(50, _currentDelayMs * 0.85);
        sender.Interval = TimeSpan.FromMilliseconds(_currentDelayMs);
    }

    private void OnPointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed)
        {
            _pressed = false;
            _repeatTimer?.Stop();
            RootBorder.ReleasePointerCapture(e.Pointer);
        }
        ApplyVisualState();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hovered = true;
        ApplyVisualState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed) return;
        _hovered = false;
        ApplyVisualState();
    }
}
