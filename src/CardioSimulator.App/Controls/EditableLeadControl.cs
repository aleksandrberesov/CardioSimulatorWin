using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Win2D editing surface for a single lead: renders the static trace, the significant-point
/// overlay, and a red ring + cross on the selected sample. Tapping or dragging selects the
/// nearest sample (its ADC value is then edited via the <see cref="EditorControlPanel"/>).
/// Port of the Android <c>EditableLead</c> + <c>SampleHandleOverlay</c>.
/// </summary>
public sealed class EditableLeadControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private LeadStream? _stream;
    private int _baseline = 1024;
    private MonitorModeModel _mode = new();
    private IReadOnlyList<SignificantPoint> _significantPoints = Array.Empty<SignificantPoint>();
    private int? _selectedIndex;
    private bool _dragging;
    private int _lastIndex = -1;

    /// <summary>Raised with the selected sample index on tap/drag.</summary>
    public event Action<int>? IndexSelected;

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

    public void SetData(
        LeadStream? stream,
        int baseline,
        MonitorModeModel mode,
        IReadOnlyList<SignificantPoint> significantPoints,
        int? selectedIndex)
    {
        _stream = stream;
        _baseline = baseline;
        _mode = mode;
        _significantPoints = significantPoints;
        _selectedIndex = selectedIndex;
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
            args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
            _stream, _baseline, _mode, _significantPoints, _selectedIndex);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _lastIndex = -1;
        _canvas.CapturePointer(e.Pointer);
        SelectAt(e.GetCurrentPoint(_canvas).Position.X);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) SelectAt(e.GetCurrentPoint(_canvas).Position.X);
    }

    private void SelectAt(double x)
    {
        if (_stream is null) return;
        var stepX = CurrentScale().PxPerSample;
        if (stepX <= 0) return;
        var index = (int)Math.Round((x - EcgRenderer.CalAreaWidth) / stepX);
        index = Math.Clamp(index, 0, _stream.Samples.Length - 1);
        if (index == _lastIndex) return;
        _lastIndex = index;
        IndexSelected?.Invoke(index);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }
}
