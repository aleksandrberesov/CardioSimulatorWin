using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// A multi-lead ECG monitor backed by a single Win2D <see cref="CanvasControl"/>.
/// Set <see cref="Waveforms"/> and <see cref="Mode"/>; the surface redraws on change.
/// </summary>
public sealed class EcgMonitorControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private IReadOnlyDictionary<Lead, Points> _waveforms = new Dictionary<Lead, Points>();
    private MonitorModeModel _mode = new();

    public EcgMonitorControl()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    public IReadOnlyDictionary<Lead, Points> Waveforms
    {
        get => _waveforms;
        set
        {
            _waveforms = value;
            _canvas.Invalidate();
        }
    }

    public MonitorModeModel Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            _canvas.Invalidate();
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EcgRenderer.Render(
            args.DrawingSession,
            (float)sender.Size.Width,
            (float)sender.Size.Height,
            _waveforms,
            _mode);
    }
}
