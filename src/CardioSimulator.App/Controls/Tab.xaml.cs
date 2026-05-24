using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Bordered, rounded, clickable cell mirroring the Android <c>Tab</c> composable.
/// Shows an icon <see cref="Glyph"/>, or <see cref="Text"/> with optional stacked
/// <see cref="SubText"/>.
/// </summary>
public sealed partial class Tab : UserControl
{
    public event EventHandler? Click;

    public Tab()
    {
        InitializeComponent();
        UpdateContent();
    }

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

    private void OnTapped(object sender, TappedRoutedEventArgs e) => Click?.Invoke(this, EventArgs.Empty);
}
