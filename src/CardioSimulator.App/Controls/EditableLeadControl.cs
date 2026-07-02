using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Win2D editing surface for a single lead: renders the static trace, the significant-point
/// overlay, and a red ring + cross on the selected sample. Tapping or dragging selects the
/// nearest sample (its ADC value is then edited via the <see cref="ConstructorControlPanel"/>).
/// In <see cref="ToolMode.Position"/> the drag instead pans an underlay reference image,
/// firing <see cref="ImageOffsetChanged"/>; the ViewModel writes the new offset back via its
/// transform setters and the next draw picks it up. Port of the Android <c>EditableLead</c>
/// + <c>SampleHandleOverlay</c>.
/// Mouse-wheel zooms (1–5×); right-click drag pans the view in any tool. In
/// <see cref="ToolMode.Pan"/> left-click drag also pans (with a move cursor); the other tools
/// keep left-click/drag for their editing behaviour.
/// </summary>
public sealed class EditableLeadControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private LeadStream? _stream;
    private int _baseline = 1024;
    private MonitorModeModel _mode = new();
    private IReadOnlyList<SignificantPoint> _significantPoints = Array.Empty<SignificantPoint>();
    private int? _selectedIndex;
    private PhotoTransform? _imageTransform;
    private CanvasBitmap? _referenceImage;
    private string? _referenceImageUri;
    private ToolMode _toolMode = ToolMode.Select;

    private int[]? _ghostTrace;
    private float? _timeRulerSeconds;
    private bool _dragging;
    private int _lastIndex = -1;
    private int _lastTraceIndex = -1;
    private Point _dragStartPos;
    private (float X, float Y) _dragStartOffset;

    // View zoom/pan state — applied inside the Win2D draw, mirroring MonitorView/EcgMonitorControl.
    private float _viewScale = 1f;
    private double _viewOffsetX;
    private double _viewOffsetY;
    private bool _panning;
    private Point _lastPanPointer;

    /// <summary>Raised with the selected sample index on tap/drag in <see cref="ToolMode.Select"/>.</summary>
    public event Action<int>? IndexSelected;

    /// <summary>Raised with the new image (OffsetX, OffsetY) on drag in <see cref="ToolMode.Position"/>.</summary>
    public event Action<float, float>? ImageOffsetChanged;

    /// <summary>Raised when a Trace-mode stroke begins (pointer down). Pair with <see cref="TraceUpdates"/>.</summary>
    public event Action? StrokeStarted;

    /// <summary>Raised with batched (sampleIndex → newAdcValue) updates during a Trace-mode drag.</summary>
    public event Action<IReadOnlyDictionary<int, int>>? TraceUpdates;

    public EditableLeadControl()
    {
        // Zoom/pan are applied inside the Win2D draw (passed to EcgRenderer.RenderEditableLead),
        // not via a XAML transform, so the trace stays crisp and stroke widths stay visually
        // constant at every zoom; the Grid (this) clips the canvas to its own bounds.
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        SizeChanged += (_, _) => UpdateViewClip();

        // Pointer events on this (Grid) so the zoom/pan layer intercepts everything;
        // coordinates are converted back to canvas-local space before editing logic runs.
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => { _dragging = false; _panning = false; ReleasePointerCapture(e.Pointer); };
        PointerCaptureLost += (_, _) => { _dragging = false; _panning = false; };
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    public void SetData(
        LeadStream? stream,
        int baseline,
        MonitorModeModel mode,
        IReadOnlyList<SignificantPoint> significantPoints,
        int? selectedIndex,
        PhotoTransform? imageTransform = null,
        ToolMode toolMode = ToolMode.Select,
        int[]? ghostTrace = null,
        float? timeRulerSeconds = null)
    {
        _stream = stream;
        _baseline = baseline;
        _mode = mode;
        _significantPoints = significantPoints;
        _selectedIndex = selectedIndex;
        _imageTransform = imageTransform;
        _toolMode = toolMode;
        _ghostTrace = ghostTrace;
        _timeRulerSeconds = timeRulerSeconds;
        UpdateCursor();
        _canvas.Invalidate();
    }

    /// <summary>Resets the view to 1× zoom and recentres it. Used by the Pan tool panel.</summary>
    public void ResetView()
    {
        _viewScale = 1f;
        _viewOffsetX = 0;
        _viewOffsetY = 0;
        ApplyViewTransform();
    }

    /// <summary>Loads a reference image into the canvas (file path). Pass null/empty to clear.</summary>
    public async Task SetReferenceImageAsync(string? uri)
    {
        _referenceImageUri = uri;
        _referenceImage = null;
        if (!string.IsNullOrEmpty(uri))
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(uri);
                using var stream = await file.OpenReadAsync();
                _referenceImage = await CanvasBitmap.LoadAsync(_canvas, stream);
            }
            catch
            {
                _referenceImage = null;
            }
        }
        _canvas.Invalidate();
    }

    /// <summary>Currently loaded reference-image bitmap (null when no image picked).</summary>
    public CanvasBitmap? ReferenceImage => _referenceImage;

    // Reloads the reference image on Win2D device recreation so the bitmap stays valid.
    private async void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _referenceImage = null;
        if (string.IsNullOrEmpty(_referenceImageUri)) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_referenceImageUri);
            using var stream = await file.OpenReadAsync();
            _referenceImage = await CanvasBitmap.LoadAsync(sender, stream);
        }
        catch
        {
            _referenceImage = null;
        }
        sender.Invalidate();
    }

    private PixelScale CurrentScale() => new(EcgRenderer.PxPerMm(_mode.DisplayScale), _mode.Speed, 1f, _mode.Calibration);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_stream is null)
        {
            args.DrawingSession.Clear(EcgColors.Palette(_mode.GridScheme, _mode.BlankSheet).Background);
            return;
        }
        try
        {
            EcgRenderer.RenderEditableLead(
                args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
                _stream, _baseline, _mode, _significantPoints, _selectedIndex, _imageTransform, _referenceImage, _ghostTrace,
                _viewScale, (float)_viewOffsetX, (float)_viewOffsetY, _timeRulerSeconds);
        }
        catch (Exception)
        {
            // Bitmap became invalid (device recreated between load and draw); discard it.
            // CreateResources will reload it on the next device-ready cycle.
            _referenceImage = null;
            args.DrawingSession.Clear(EcgColors.Palette(_mode.GridScheme, _mode.BlankSheet).Background);
        }
    }

    // ── Zoom/pan ────────────────────────────────────────────────────────────

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.1f : 1f / 1.1f;
        _viewScale = Math.Clamp(_viewScale * factor, 1f, 5f);
        ClampViewOffset();
        ApplyViewTransform();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        // Right-button drag pans in any tool; in Pan mode the left button pans too.
        if (point.Properties.IsRightButtonPressed || _toolMode == ToolMode.Pan)
        {
            _panning = true;
            _lastPanPointer = point.Position;
            CapturePointer(e.Pointer);
            return;
        }

        _dragging = true;
        var pos = ToCanvasCoords(point.Position);
        CapturePointer(e.Pointer);

        if (_toolMode == ToolMode.Position && _referenceImage is not null)
        {
            _dragStartPos = pos;
            _dragStartOffset = _imageTransform is null
                ? (0f, 0f)
                : (_imageTransform.OffsetX, _imageTransform.OffsetY);
        }
        else if (_toolMode == ToolMode.Trace && _stream is not null)
        {
            StrokeStarted?.Invoke();
            var scale = CurrentScale();
            var idx = ClampIndex((int)Math.Round((pos.X - EcgRenderer.TraceLeft(scale)) / scale.PxPerSample));
            _lastTraceIndex = idx;
            var adc = AdcAt(pos.Y, scale);
            TraceUpdates?.Invoke(new Dictionary<int, int> { [idx] = adc });
        }
        else
        {
            _lastIndex = -1;
            SelectAt(pos.X);
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            var p = e.GetCurrentPoint(this).Position;
            _viewOffsetX += p.X - _lastPanPointer.X;
            _viewOffsetY += p.Y - _lastPanPointer.Y;
            _lastPanPointer = p;
            ClampViewOffset();
            ApplyViewTransform();
            return;
        }

        if (!_dragging) return;
        var pos = ToCanvasCoords(e.GetCurrentPoint(this).Position);

        if (_toolMode == ToolMode.Position && _referenceImage is not null)
        {
            var newX = _dragStartOffset.X + (float)(pos.X - _dragStartPos.X);
            var newY = _dragStartOffset.Y + (float)(pos.Y - _dragStartPos.Y);
            ImageOffsetChanged?.Invoke(newX, newY);
        }
        else if (_toolMode == ToolMode.Trace && _stream is not null)
        {
            var scale = CurrentScale();
            var idx = ClampIndex((int)Math.Round((pos.X - EcgRenderer.TraceLeft(scale)) / scale.PxPerSample));
            var adc = AdcAt(pos.Y, scale);
            var dict = new Dictionary<int, int>();

            if (_lastTraceIndex >= 0 && idx != _lastTraceIndex)
            {
                // Linearly interpolate ADC across any sample columns the cursor skipped.
                var lastAdc = _stream.Samples[ClampIndex(_lastTraceIndex)];
                var step = idx > _lastTraceIndex ? 1 : -1;
                var span = Math.Abs(idx - _lastTraceIndex);
                for (var i = 1; i <= span; i++)
                {
                    var thisIdx = _lastTraceIndex + i * step;
                    var t = (float)i / span;
                    var thisAdc = (int)MathF.Round(lastAdc + (adc - lastAdc) * t);
                    dict[thisIdx] = thisAdc;
                }
            }
            else
            {
                dict[idx] = adc;
            }

            _lastTraceIndex = idx;
            if (dict.Count > 0) TraceUpdates?.Invoke(dict);
        }
        else
        {
            SelectAt(pos.X);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _panning = false;
        ReleasePointerCapture(e.Pointer);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private int ClampIndex(int index) =>
        _stream is null || _stream.Samples.Length == 0 ? 0 : Math.Clamp(index, 0, _stream.Samples.Length - 1);

    /// <summary>Inverse of the trace-draw mapping: <c>adc = baseline + (baselineY - y) / stepY</c>.</summary>
    private int AdcAt(double y, PixelScale scale)
    {
        var baselineY = (float)_canvas.ActualHeight / 2f;
        if (scale.PxPerAdcCount <= 0) return _baseline;
        return _baseline + (int)Math.Round((baselineY - y) / scale.PxPerAdcCount);
    }

    private void SelectAt(double x)
    {
        if (_stream is null || _stream.Samples.Length == 0) return;
        var scale = CurrentScale();
        var stepX = scale.PxPerSample;
        if (stepX <= 0) return;
        var index = (int)Math.Round((x - EcgRenderer.TraceLeft(scale)) / stepX);
        index = Math.Clamp(index, 0, _stream.Samples.Length - 1);
        if (index == _lastIndex) return;
        _lastIndex = index;
        IndexSelected?.Invoke(index);
    }

    // Converts a point from Grid (outer) coordinates to canvas-local coordinates by
    // inverting the scale-around-centre + translate render transform applied to _canvas.
    private Point ToCanvasCoords(Point gridPt)
    {
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        return new Point(
            (gridPt.X - _viewOffsetX - w / 2) / _viewScale + w / 2,
            (gridPt.Y - _viewOffsetY - h / 2) / _viewScale + h / 2);
    }

    private void ClampViewOffset()
    {
        var maxX = ActualWidth * (_viewScale - 1) / 2;
        var maxY = ActualHeight * (_viewScale - 1) / 2;
        _viewOffsetX = Math.Clamp(_viewOffsetX, -maxX, maxX);
        _viewOffsetY = Math.Clamp(_viewOffsetY, -maxY, maxY);
    }

    private void ApplyViewTransform() => _canvas.Invalidate();

    private void UpdateViewClip() =>
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };

    // Shows a move cursor while the Pan tool is active so the drag affordance is discoverable.
    private void UpdateCursor() =>
        ProtectedCursor = _toolMode == ToolMode.Pan
            ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll)
            : null;
}
