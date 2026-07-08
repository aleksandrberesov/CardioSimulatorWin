using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The «Собери ЭКГ» workspace (left pane of the Testing screen for an assembly question): three columns
/// — P, QRS and T — each with an empty <em>slot</em> on the tape (a gray isoline) above a palette of
/// candidate waveform tiles. The student drags (or taps) the right tile into each slot to reconstruct
/// the complex called for by the assignment. After the answer is checked the slots lock and colour
/// green/red, and a wrong slot also shows the correct morphology faintly for feedback.
/// </summary>
/// <remarks>
/// Purely a view over a <see cref="AssemblyAttempt"/>: it mutates the attempt's placements and raises
/// <see cref="PlacementChanged"/>; the owning screen re-pushes the attempt via <see cref="SetAttempt"/>
/// to redraw. Pieces render as lightweight <see cref="Polyline"/>s (no Win2D), each block scaled by the
/// tallest / longest piece in that block so variants stay comparable within a column.
/// </remarks>
public sealed class EcgAssemblyControl : UserControl
{
    private const double SlotWidth = 158;
    private const double SlotHeight = 84;
    private const double TileWidth = 148;
    private const double TileHeight = 54;

    private readonly StackPanel _root = new() { Spacing = 12, Padding = new Thickness(16) };

    private AssemblyAttempt? _attempt;
    private bool _revealed;

    // Per-block render scales (amplitude peak + longest run), so a column's variants share a scale.
    private readonly Dictionary<EcgBlock, (double MaxAbs, int MaxLen)> _scales = new();

    // Tap-to-place fallback: the tile picked up by a first tap, placed by a tap on its column.
    private AssemblyPaletteItem? _selected;

    private static readonly Brush TraceBrush = new SolidColorBrush(Color.FromArgb(255, 0x24, 0x2A, 0x30));
    private static readonly Brush IsolineBrush = new SolidColorBrush(Color.FromArgb(150, 0x9A, 0xA0, 0xA6));
    private static readonly Brush PaperBrush = new SolidColorBrush(Color.FromArgb(255, 0xFB, 0xFB, 0xF6));
    private static readonly Brush SlotBorderBrush = new SolidColorBrush(Color.FromArgb(120, 0x9A, 0xA0, 0xA6));
    private static readonly Brush TileBorderBrush = new SolidColorBrush(Color.FromArgb(70, 0x33, 0xA0, 0x6A));
    private static readonly Brush SelectedBrush = AppTheme.Accent;

    /// <summary>Raised when the student places or removes a piece (so the panel can update the Check button).</summary>
    public event Action? PlacementChanged;

    public EcgAssemblyControl()
    {
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _root,
        };
    }

    /// <summary>Binds the current attempt (null clears the workspace) and whether the answer is revealed.</summary>
    public void SetAttempt(AssemblyAttempt? attempt, bool revealed)
    {
        _attempt = attempt;
        _revealed = revealed;
        _selected = null;
        ComputeScales();
        Render();
    }

    private void ComputeScales()
    {
        _scales.Clear();
        if (_attempt is null) return;
        foreach (var block in _attempt.Blocks)
        {
            double maxAbs = 0;
            var maxLen = 1;
            foreach (var item in _attempt.Palette(block))
            {
                var s = item.Piece.SampleList;
                if (s.Count > maxLen) maxLen = s.Count;
                foreach (var v in s) { var a = Math.Abs(v); if (a > maxAbs) maxAbs = a; }
            }
            _scales[block] = (maxAbs, maxLen);
        }
    }

    private void Render()
    {
        _root.Children.Clear();
        if (_attempt is null) return;

        _root.Children.Add(new TextBlock
        {
            Text = AppStrings.AssembleTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = AppTheme.TextPrimary,
        });
        _root.Children.Add(new TextBlock
        {
            Text = _revealed ? AppStrings.AssembleRevealHint : AppStrings.AssembleHint,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.TextSecondary,
        });

        var columns = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < _attempt.Blocks.Count; i++)
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < _attempt.Blocks.Count; i++)
        {
            var col = BuildColumn(_attempt.Blocks[i]);
            Grid.SetColumn(col, i);
            columns.Children.Add(col);
        }
        _root.Children.Add(columns);
    }

    private FrameworkElement BuildColumn(EcgBlock block)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 12, 0), Width = SlotWidth };

        stack.Children.Add(new TextBlock
        {
            Text = BlockLabel(block),
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = AppTheme.Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(BuildSlot(block));

        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.AssemblePieces,
            FontSize = 11,
            Foreground = AppTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        foreach (var item in _attempt!.Available(block))
            stack.Children.Add(BuildTile(item));

        return stack;
    }

    // ── Slot (drop target on the tape) ────────────────────────────────────────

    private UIElement BuildSlot(EcgBlock block)
    {
        var placed = _attempt!.Placed(block);

        var host = new Grid { Width = SlotWidth, Height = SlotHeight };
        host.Children.Add(Isoline(SlotWidth, SlotHeight));

        Brush border = SlotBorderBrush;
        var borderThickness = 1.0;
        var dashed = placed is null;

        if (placed is not null)
        {
            if (_revealed)
            {
                var ok = placed.IsCorrect;
                border = ok ? AppTheme.Positive : AppTheme.Negative;
                borderThickness = 2.0;
                // Show the chosen morphology; if wrong, also show the correct one faintly for feedback.
                if (!ok && _attempt.Spec.Of(block) is { } spec)
                    host.Children.Add(Trace(spec.Correct.SampleList, block, SlotWidth, SlotHeight,
                        AppTheme.Positive, thickness: 1.0, opacity: 0.5));
                host.Children.Add(Trace(placed.Piece.SampleList, block, SlotWidth, SlotHeight,
                    ok ? AppTheme.Positive : AppTheme.Negative));
            }
            else
            {
                border = AppTheme.Accent;
                host.Children.Add(Trace(placed.Piece.SampleList, block, SlotWidth, SlotHeight, TraceBrush));
            }
        }

        var frame = new Border
        {
            Width = SlotWidth,
            Height = SlotHeight,
            Background = PaperBrush,
            BorderBrush = border,
            BorderThickness = new Thickness(borderThickness),
            CornerRadius = new CornerRadius(6),
            Child = host,
            AllowDrop = !_revealed,
        };

        if (dashed)
        {
            // A visual cue that the slot is empty and awaiting a piece.
            frame.BorderBrush = SlotBorderBrush;
        }

        if (!_revealed)
        {
            frame.DragOver += (_, e) =>
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                if (e.DragUIOverride is not null) e.DragUIOverride.IsCaptionVisible = false;
            };
            frame.Drop += OnSlotDrop;
            frame.Tapped += (_, _) =>
            {
                if (placed is not null) { _attempt.Clear(block); RaiseChanged(); }
                else if (_selected is not null) { _attempt.Place(_selected); _selected = null; RaiseChanged(); }
            };
        }

        return frame;
    }

    private async void OnSlotDrop(object sender, DragEventArgs e)
    {
        if (_attempt is null || _revealed) return;
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        try
        {
            var text = await e.DataView.GetTextAsync();
            if (int.TryParse(text, out var key) && _attempt.ItemByKey(key) is { } item)
            {
                // Route by the piece's own block, so a piece always lands in its column's slot.
                _attempt.Place(item);
                RaiseChanged();
            }
        }
        catch
        {
            // Ignore malformed drop payloads.
        }
    }

    // ── Palette tile (draggable source) ───────────────────────────────────────

    private UIElement BuildTile(AssemblyPaletteItem item)
    {
        var host = new Grid { Width = TileWidth, Height = TileHeight };
        host.Children.Add(Isoline(TileWidth, TileHeight));
        host.Children.Add(Trace(item.Piece.SampleList, item.Block, TileWidth, TileHeight, TraceBrush));

        var isSelected = _selected is not null && _selected.Key == item.Key;
        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Background = PaperBrush,
            BorderBrush = isSelected ? SelectedBrush : TileBorderBrush,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = host,
            CanDrag = !_revealed,
        };

        if (!_revealed)
        {
            var key = item.Key;
            tile.DragStarting += (_, args) =>
            {
                args.Data.SetText(key.ToString());
                args.Data.RequestedOperation = DataPackageOperation.Move;
            };
            tile.Tapped += (_, _) =>
            {
                _selected = _selected is not null && _selected.Key == item.Key ? null : item;
                Render();
            };
        }

        return tile;
    }

    // ── Rendering helpers ─────────────────────────────────────────────────────

    private static Line Isoline(double w, double h) => new()
    {
        X1 = 6,
        Y1 = h / 2,
        X2 = w - 6,
        Y2 = h / 2,
        Stroke = IsolineBrush,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 3, 3 },
    };

    private Polyline Trace(
        IReadOnlyList<int> samples, EcgBlock block, double boxW, double boxH,
        Brush stroke, double thickness = 1.5, double opacity = 1.0)
    {
        var (maxAbs, maxLen) = _scales.TryGetValue(block, out var s) ? s : (0d, 1);
        var mid = boxH / 2;
        var usableHalf = boxH * 0.4;
        var ampScale = maxAbs > 0 ? usableHalf / maxAbs : 0;
        var pad = 6.0;
        var span = Math.Max(1, maxLen - 1);
        // Width proportional to duration (samples / longest run in this block), so wide beats read wide.
        var width = (boxW - 2 * pad) * (samples.Count <= 1 ? 1.0 : (samples.Count - 1) / (double)span);

        var points = new PointCollection();
        if (samples.Count == 1)
        {
            points.Add(new Point(pad, mid - samples[0] * ampScale));
            points.Add(new Point(pad + width, mid - samples[0] * ampScale));
        }
        else
        {
            for (var i = 0; i < samples.Count; i++)
            {
                var x = pad + width * i / (samples.Count - 1);
                var y = mid - samples[i] * ampScale;
                points.Add(new Point(x, y));
            }
        }

        return new Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = opacity,
        };
    }

    private void RaiseChanged() => PlacementChanged?.Invoke();

    private static string BlockLabel(EcgBlock block) => block switch
    {
        EcgBlock.P => AppStrings.AssembleBlockP,
        EcgBlock.QRS => AppStrings.AssembleBlockQrs,
        EcgBlock.T => AppStrings.AssembleBlockT,
        _ => block.ToString(),
    };
}
