using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Bordered, rounded, clickable cell mirroring the Android <c>Tab</c> composable.
/// Shows an icon <see cref="Glyph"/>, or <see cref="Text"/> with optional stacked
/// <see cref="SubText"/>. When <see cref="IsRepeatable"/> is set, holding it fires
/// <see cref="Click"/> repeatedly with acceleration (Android <c>repeatingClickable</c>:
/// immediate, then a 600 ms delay, then ~200 ms accelerating to a 50 ms floor).
/// </summary>
public sealed partial class Tab : UserControl
{
    public event EventHandler? Click;

    private DispatcherQueueTimer? _repeatTimer;
    private long _pressStartMs;
    private double _currentDelayMs;
    private bool _pressed;

    public Tab()
    {
        InitializeComponent();
        UpdateContent();
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

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Tab)d).UpdateContent();

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

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsRepeatable) return; // repeatable taps are driven by the pointer-press loop
        Click?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsRepeatable) return;
        _pressed = true;
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
        if (!_pressed) return;
        _pressed = false;
        _repeatTimer?.Stop();
        RootBorder.ReleasePointerCapture(e.Pointer);
        RootBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            new Windows.UI.Color { A = 30, R = 128, G = 128, B = 128 });
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RootBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            new Windows.UI.Color { A = 30, R = 128, G = 128, B = 128 });
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed) return;
        RootBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });
        OnPointerUp(sender, e);
    }
}
