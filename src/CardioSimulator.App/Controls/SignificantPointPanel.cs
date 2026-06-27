using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Right-hand editor panel for marking significant ECG points on the selected sample, grouped
/// by wave (P / QRS / T), plus an R-R interval list when ≥2 R peaks exist. Port of the Android
/// <c>SignificantPointPanel</c>.
/// </summary>
public sealed class SignificantPointPanel : UserControl
{
    private static readonly (string Group, EcgPointType[] Types)[] Groups =
    {
        ("P", new[] { EcgPointType.P_START, EcgPointType.P_PEAK, EcgPointType.P_END }),
        ("QRS", new[] { EcgPointType.QRS_START, EcgPointType.Q_PEAK, EcgPointType.R_PEAK, EcgPointType.S_PEAK, EcgPointType.QRS_END }),
        ("T", new[] { EcgPointType.T_START, EcgPointType.T_PEAK, EcgPointType.T_END }),
    };

    private readonly StackPanel _root = new() { Padding = new Thickness(8), Spacing = 8 };
    private IReadOnlyList<SignificantPoint> _points = Array.Empty<SignificantPoint>();
    private int? _selectedIndex;
    private float _sampleRate = 500f;

    /// <summary>Raised with (sample index, point type) when a chip is toggled.</summary>
    public event Action<int, EcgPointType>? PointToggle;
    public event Action? AutoDetectClick;

    public SignificantPointPanel()
    {
        Width = 150;
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Rebuild();
    }

    public void SetData(IReadOnlyList<SignificantPoint> points, int? selectedIndex, float sampleRate)
    {
        _points = points;
        _selectedIndex = selectedIndex;
        _sampleRate = sampleRate;
        Rebuild();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        _root.Children.Add(new TextBlock { Text = AppStrings.EditorSignificantPoints, FontWeight = FontWeights.SemiBold });
        _root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.Gray) });

        var autoDetectBtn = new Button
        {
            Content = "Auto-Detect",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Lavender),
        };
        autoDetectBtn.Click += (_, _) => AutoDetectClick?.Invoke();
        _root.Children.Add(autoDetectBtn);

        if (_selectedIndex is { } sel)
        {
            _root.Children.Add(new TextBlock { Text = AppStrings.EditorSampleLabel(sel), FontSize = 12 });
            foreach (var (group, types) in Groups)
            {
                _root.Children.Add(new TextBlock
                {
                    Text = GroupTitle(group),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.SteelBlue),
                    Margin = new Thickness(0, 4, 0, 0),
                });
                foreach (var type in types)
                {
                    var captured = type;
                    var chip = new ToggleButton
                    {
                        Content = AppStrings.EcgPointLabel(type),
                        IsChecked = _points.Any(p => p.Index == sel && p.Type == type),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        FontSize = 11,
                        Padding = new Thickness(4, 2, 4, 2),
                    };
                    chip.Click += (_, _) => PointToggle?.Invoke(sel, captured);
                    _root.Children.Add(chip);
                }
            }
        }
        else
        {
            _root.Children.Add(new TextBlock
            {
                Text = AppStrings.EditorSelectPointHint,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.Gray),
            });
        }

        var rPeaks = _points.Where(p => p.Type == EcgPointType.R_PEAK).OrderBy(p => p.Index).ToList();
        if (rPeaks.Count >= 2)
        {
            _root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Colors.Gray) });
            _root.Children.Add(new TextBlock { Text = AppStrings.EditorRhythmsTitle, FontWeight = FontWeights.SemiBold });
            var rrColor = new Color { A = 255, R = 0x2E, G = 0x7D, B = 0x32 };
            for (var i = 0; i + 1 < rPeaks.Count; i++)
            {
                var duration = (rPeaks[i + 1].Index - rPeaks[i].Index) / _sampleRate;
                _root.Children.Add(new TextBlock
                {
                    Text = AppStrings.EcgRrValueFormat(duration),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(rrColor),
                });
            }
        }
    }

    private static string GroupTitle(string group) => group switch
    {
        "P" => AppStrings.EditorPWave,
        "QRS" => AppStrings.EditorQrsComplex,
        "T" => AppStrings.EditorTWave,
        _ => group,
    };
}
