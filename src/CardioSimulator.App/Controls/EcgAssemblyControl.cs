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
/// The «Собери ЭКГ» workspace (left pane of the Testing screen for an assembly question). The trace was
/// cut into several contiguous parts and shuffled; the student drags (or taps) the parts back into the
/// right order along one continuous ECG <em>tape</em> to rebuild the original strip. A correct ordering
/// reads as one smooth continuous line; a wrong one shows visible steps between parts. After the answer
/// is checked the slots lock and tint green/red, with the correct part shown faintly where it's wrong.
/// </summary>
/// <remarks>
/// Purely a view over an <see cref="AssemblyAttempt"/>: it mutates the attempt's placements and raises
/// <see cref="PlacementChanged"/>; the owning screen re-pushes the attempt via <see cref="SetAttempt"/>
/// to redraw. Parts render as lightweight <see cref="Polyline"/>s (no Win2D), all sharing one amplitude
/// scale (they come from one trace) so a correct ordering joins seamlessly.
/// </remarks>
public sealed class EcgAssemblyControl : UserControl
{
    private const double MaxBoardWidth = 640;
    private const double MaxSlotWidth = 132;
    private const double TapeHeight = 148;
    private const double TileHeight = 88;

    private readonly StackPanel _root = new()
    {
        Spacing = 14,
        Padding = new Thickness(20),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private AssemblyAttempt? _attempt;
    private bool _revealed;
    private double _maxAbs = 1;   // shared amplitude scale across every part

    // Tap-to-place fallback: the part picked up by a first tap, placed by a tap on any slot.
    private AssemblyPaletteItem? _selected;

    private static readonly Brush TraceBrush = new SolidColorBrush(Color.FromArgb(255, 0x24, 0x2A, 0x30));
    private static readonly Brush IsolineBrush = new SolidColorBrush(Color.FromArgb(150, 0x9A, 0xA0, 0xA6));
    private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromArgb(60, 0x9A, 0xA0, 0xA6));
    private static readonly Brush PaperBrush = new SolidColorBrush(Color.FromArgb(255, 0xFB, 0xFB, 0xF6));
    private static readonly Brush TapeBorderBrush = new SolidColorBrush(Color.FromArgb(120, 0x9A, 0xA0, 0xA6));
    private static readonly Brush TileBorderBrush = new SolidColorBrush(Color.FromArgb(70, 0x33, 0xA0, 0x6A));
    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush SelectedBrush = AppTheme.Accent;

    /// <summary>Raised when the student places or removes a part (so the panel can update the Check button).</summary>
    public event Action? PlacementChanged;

    public EcgAssemblyControl()
    {
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = _root,
        };
    }

    /// <summary>Binds the current attempt (null clears the workspace) and whether the answer is revealed.</summary>
    public void SetAttempt(AssemblyAttempt? attempt, bool revealed)
    {
        _attempt = attempt;
        _revealed = revealed;
        _selected = null;
        ComputeScale();
        Render();
    }

    private void ComputeScale()
    {
        _maxAbs = 1;
        if (_attempt is null) return;
        foreach (var item in _attempt.Palette)
            foreach (var v in item.Samples)
            {
                var a = Math.Abs(v);
                if (a > _maxAbs) _maxAbs = a;
            }
    }

    private double SlotWidth => _attempt is { SlotCount: > 0 }
        ? Math.Min(MaxSlotWidth, MaxBoardWidth / _attempt.SlotCount)
        : MaxSlotWidth;

    private void Render()
    {
        _root.Children.Clear();
        if (_attempt is null) return;

        var slotW = SlotWidth;
        var boardW = slotW * _attempt.SlotCount;

        _root.Children.Add(new TextBlock
        {
            Text = AppStrings.AssembleTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 17,
            Foreground = AppTheme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _root.Children.Add(new TextBlock
        {
            Text = _revealed ? AppStrings.AssembleRevealHint : AppStrings.AssembleHint,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = Math.Max(boardW, 240),
            Foreground = AppTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        _root.Children.Add(BuildTape(slotW, boardW));

        _root.Children.Add(new TextBlock
        {
            Text = AppStrings.AssemblePieces,
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        _root.Children.Add(BuildPalette(slotW, boardW));
    }

    // ── The tape: one continuous isoline + ordered drop slots ──────────────────

    private UIElement BuildTape(double slotW, double boardW)
    {
        var inner = new Grid { Width = boardW, Height = TapeHeight };

        inner.Children.Add(new Line
        {
            X1 = 6,
            Y1 = TapeHeight / 2,
            X2 = boardW - 6,
            Y2 = TapeHeight / 2,
            Stroke = IsolineBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        });

        for (var i = 1; i < _attempt!.SlotCount; i++)
        {
            inner.Children.Add(new Line
            {
                X1 = i * slotW,
                Y1 = 10,
                X2 = i * slotW,
                Y2 = TapeHeight - 10,
                Stroke = DividerBrush,
                StrokeThickness = 1,
            });
        }

        var slots = new Grid();
        for (var i = 0; i < _attempt.SlotCount; i++)
        {
            slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var slot = BuildSlot(i, slotW);
            Grid.SetColumn(slot, i);
            slots.Children.Add(slot);
        }
        inner.Children.Add(slots);

        return new Border
        {
            Width = boardW,
            Height = TapeHeight,
            Background = PaperBrush,
            BorderBrush = TapeBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = inner,
        };
    }

    private FrameworkElement BuildSlot(int slotIndex, double slotW)
    {
        var placed = _attempt!.PlacedAt(slotIndex);
        var host = new Grid { Background = TransparentBrush };

        if (placed is not null)
        {
            if (_revealed)
            {
                var ok = placed.CorrectIndex == slotIndex;
                host.Background = new SolidColorBrush(ok
                    ? Color.FromArgb(36, 0x2E, 0x7D, 0x32)
                    : Color.FromArgb(36, 0xC6, 0x28, 0x28));
                // Where wrong, show the part that belongs here faintly behind the placed one.
                if (!ok && slotIndex < _attempt.Spec.PartCount)
                    host.Children.Add(Trace(_attempt.Spec.PartList[slotIndex].SampleList, slotW, TapeHeight,
                        AppTheme.Positive, thickness: 1.3, opacity: 0.4));
                host.Children.Add(Trace(placed.Samples, slotW, TapeHeight,
                    ok ? AppTheme.Positive : AppTheme.Negative, thickness: 1.8));
            }
            else
            {
                host.Children.Add(Trace(placed.Samples, slotW, TapeHeight, TraceBrush, thickness: 1.8));
            }
        }

        if (!_revealed)
        {
            host.AllowDrop = true;
            host.DragOver += (_, e) =>
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                if (e.DragUIOverride is not null) e.DragUIOverride.IsCaptionVisible = false;
            };
            host.Drop += (s, e) => OnSlotDrop(slotIndex, e);
            host.Tapped += (_, _) =>
            {
                if (_attempt.PlacedAt(slotIndex) is not null) { _attempt.Clear(slotIndex); RaiseChanged(); }
                else if (_selected is not null) { _attempt.Place(slotIndex, _selected); _selected = null; RaiseChanged(); }
            };
        }

        return host;
    }

    private async void OnSlotDrop(int slotIndex, DragEventArgs e)
    {
        if (_attempt is null || _revealed) return;
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        try
        {
            var text = await e.DataView.GetTextAsync();
            if (int.TryParse(text, out var key) && _attempt.ItemByKey(key) is { } item)
            {
                _attempt.Place(slotIndex, item);
                RaiseChanged();
            }
        }
        catch
        {
            // Ignore malformed drop payloads.
        }
    }

    // ── Palette: the shuffled parts still to be placed ─────────────────────────

    private UIElement BuildPalette(double slotW, double boardW)
    {
        // A single centered row of the remaining parts; part tiles match the slot width so they read as
        // the same pieces. The pool never exceeds the slot count, so its natural width ≤ the tape width.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = boardW,
        };
        foreach (var item in _attempt!.Available)
            row.Children.Add(BuildTile(item, slotW));
        return row;
    }

    private UIElement BuildTile(AssemblyPaletteItem item, double slotW)
    {
        var host = new Grid { Width = slotW, Height = TileHeight };
        host.Children.Add(Isoline(slotW, TileHeight));
        host.Children.Add(Trace(item.Samples, slotW, TileHeight, TraceBrush));

        var isSelected = _selected is not null && _selected.Key == item.Key;
        var tile = new Border
        {
            Width = slotW,
            Height = TileHeight,
            Background = PaperBrush,
            BorderBrush = isSelected ? SelectedBrush : TileBorderBrush,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            CornerRadius = new CornerRadius(6),
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

    /// <summary>
    /// Draws a part across the <em>full</em> box width (no horizontal padding), so adjacent parts placed
    /// in the right order join into one continuous trace. Amplitude uses the shared scale so every part
    /// keeps the same vertical proportions. Long parts are strided down to keep the polyline light.
    /// </summary>
    private Polyline Trace(
        IReadOnlyList<int> samples, double boxW, double boxH, Brush stroke,
        double thickness = 1.5, double opacity = 1.0)
    {
        var mid = boxH / 2;
        var usableHalf = boxH * 0.4;
        var ampScale = _maxAbs > 0 ? usableHalf / _maxAbs : 0;

        var count = samples.Count;
        var stride = Math.Max(1, (int)Math.Ceiling(count / Math.Max(2.0 * boxW, 1.0)));

        var points = new PointCollection();
        if (count == 1)
        {
            points.Add(new Point(0, mid - samples[0] * ampScale));
            points.Add(new Point(boxW, mid - samples[0] * ampScale));
        }
        else
        {
            for (var i = 0; i < count; i += stride)
            {
                var x = boxW * i / (count - 1);
                points.Add(new Point(x, mid - samples[i] * ampScale));
            }
            // Always include the last sample so the part spans the full width and joins its neighbour.
            var lastX = boxW;
            var lastY = mid - samples[count - 1] * ampScale;
            if (points.Count == 0 || points[^1].X < lastX) points.Add(new Point(lastX, lastY));
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
}
