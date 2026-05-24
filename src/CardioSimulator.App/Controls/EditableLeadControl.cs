using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Win2D editing surface for a single lead: renders the static trace with drag handles and
/// turns vertical drags into raw-ADC edits of the nearest sample. Port of the Android
/// <c>EditableLead</c> + <c>SampleHandleOverlay</c>.
/// </summary>
public sealed class EditableLeadControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private LeadStream? _stream;
    private int _baseline = 1024;
    private MonitorModeModel _mode = new();
    private bool _dragging;
    private double _lastY;

    /// <summary>Raised on drag with (sample index, new ADC value).</summary>
    public event Action<int, int>? SampleChanged;

    public EditableLeadControl()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += (_, _) => _dragging = false;
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    public void SetData(LeadStream? stream, int baseline, MonitorModeModel mode)
    {
        _stream = stream;
        _baseline = baseline;
        _mode = mode;
        _canvas.Invalidate();
    }

    private PixelScale CurrentScale() => new(EcgRenderer.PxPerMm(_mode.DisplayScale), _mode.Speed, 1f, _mode.Calibration);

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_stream is null)
        {
            args.DrawingSession.Clear(EcgColors.Palette(_mode.GridScheme).Background);
            return;
        }
        EcgRenderer.RenderEditableLead(
            args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height, _stream, _baseline, _mode);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _lastY = e.GetCurrentPoint(_canvas).Position.Y;
        _canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _stream is null) return;
        var pos = e.GetCurrentPoint(_canvas).Position;
        var scale = CurrentScale();
        var stepX = scale.PxPerSample;
        var stepY = scale.PxPerAdcCount;
        if (stepX <= 0 || stepY <= 0) return;

        var index = (int)Math.Round((pos.X - EcgRenderer.CalAreaWidth) / stepX);
        var samples = _stream.Samples;
        if (index < 0 || index >= samples.Length) return;

        var deltaAdc = (int)Math.Round(-(pos.Y - _lastY) / stepY);
        if (deltaAdc != 0)
        {
            SampleChanged?.Invoke(index, samples[index] + deltaAdc);
            _lastY = pos.Y;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }
}
