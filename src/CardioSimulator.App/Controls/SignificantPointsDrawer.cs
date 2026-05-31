using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Collapsible left drawer listing the current pathology's significant points (sorted by index);
/// clicking one selects that sample. Port of the Android <c>SignificantPointsDrawer</c>.
/// </summary>
public sealed class SignificantPointsDrawer : UserControl
{
    private readonly ConstructorViewModel _editorVm;
    private float _sampleRate;
    private readonly StackPanel _list = new() { Padding = new Thickness(8), Spacing = 4 };
    private readonly Border _panel;
    private readonly TextBlock _handleGlyph;
    private bool _expanded;

    public SignificantPointsDrawer(ConstructorViewModel editorVm, float sampleRate)
    {
        _editorVm = editorVm;
        _sampleRate = sampleRate;

        _panel = new Border
        {
            Width = 250,
            Background = new SolidColorBrush(Colors.WhiteSmoke),
            Child = new ScrollViewer { Content = _list },
            Visibility = Visibility.Collapsed,
        };

        _handleGlyph = new TextBlock
        {
            Text = char.ConvertFromUtf32(0xE76C),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Border
        {
            Width = 24,
            Height = 64,
            Background = new SolidColorBrush(Colors.LightSteelBlue),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            Child = _handleGlyph,
        };
        handle.Tapped += (_, _) => Toggle();

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_panel);
        row.Children.Add(handle);
        Content = row;

        _editorVm.PropertyChanged += OnVmChanged;
        Unloaded += (_, _) => _editorVm.PropertyChanged -= OnVmChanged;
    }

    public void SetSampleRate(float sampleRate) => _sampleRate = sampleRate;

    private void Toggle()
    {
        _expanded = !_expanded;
        _panel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _handleGlyph.Text = char.ConvertFromUtf32(_expanded ? 0xE76B : 0xE76C);
        if (_expanded) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_expanded &&
            (e.PropertyName == nameof(ConstructorViewModel.TargetFile) ||
             e.PropertyName == nameof(ConstructorViewModel.SelectedIndex)))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _list.Children.Add(new TextBlock { Text = AppStrings.EditorSignificantPoints, FontWeight = FontWeights.SemiBold });
        _list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.Gray) });

        var points = (_editorVm.TargetFile?.SignificantPoints ?? Array.Empty<SignificantPoint>())
            .OrderBy(p => p.Index).ToList();
        if (points.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = AppStrings.EditorSelectPointHint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 12,
            });
            return;
        }

        var sel = _editorVm.SelectedIndex;
        foreach (var point in points)
        {
            var captured = point;
            var timeMs = (int)(point.Index * 1000f / _sampleRate);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = AppStrings.EcgPointLabel(point.Type) });
            stack.Children.Add(new TextBlock { Text = AppStrings.EditorTimeFormat(timeMs), FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray) });
            var item = new Button
            {
                Content = stack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(point.Index == sel ? Colors.LightBlue : Colors.Transparent),
            };
            item.Click += (_, _) => _editorVm.SelectIndex(captured.Index);
            _list.Children.Add(item);
        }
    }
}
