using System.Diagnostics;
using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Controls;

/// <summary>
/// HR=60 looping preview strip: renders a single baseline-zeroed waveform as a continuous line
/// that tiles across the width and scrolls left at paper speed (no grid / calibration pulse /
/// lead label). Faithful port of the Android <c>PreviewPane</c> used in the editor footer.
/// </summary>
public sealed class PreviewPaneControl : Grid
{
    private readonly CanvasControl _canvas = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IReadOnlyList<float> _values = Array.Empty<float>();
    private MonitorModeModel _mode = new();

    public PreviewPaneControl()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => { if (_values.Count >= 2) _canvas.Invalidate(); };
        _timer.Start();

        Unloaded += (_, _) => { _timer.Stop(); _canvas.RemoveFromVisualTree(); };
    }

    public void SetData(IReadOnlyList<float> values, MonitorModeModel mode)
    {
        _values = values;
        _mode = mode;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_values.Count < 2) return;
        var scale = new PixelScale(EcgRenderer.PxPerMm(_mode.DisplayScale), _mode.Speed, 1f, _mode.Calibration);
        EcgRenderer.DrawPreviewTrace(
            args.DrawingSession, _values,
            (float)sender.Size.Width, (float)sender.Size.Height,
            scale, (float)_clock.Elapsed.TotalSeconds);
    }
}
