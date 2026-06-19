using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Teaching mode: the course viewer is the main panel; a monitor button in its top bar pops a
/// full-screen <see cref="MonitorViewerOverlay"/> (the Win2D monitor + rhythm drawer) over it.
/// This inverts the Android <c>TeachingScreen</c> (where the monitor is the base and the course
/// viewer is the overlay) — a deliberate Windows divergence.
/// </summary>
public sealed class TeachingScreen : UserControl
{
    private readonly CourseViewerPanel _coursePanel = new();
    private readonly MonitorViewerOverlay _monitorOverlay = new();

    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private bool _lastCompareMode;

    /// <summary>Raised when a comparison pane is tapped, carrying the pane index.</summary>
    public event EventHandler<int>? PaneTapped
    {
        add => _monitorOverlay.PaneTapped += value;
        remove => _monitorOverlay.PaneTapped -= value;
    }

    /// <summary>Raised when the monitor overlay is shown (true) or hidden (false), so the host can
    /// reveal/hide the bottom monitor control panel (hidden while the course viewer is showing).</summary>
    public event EventHandler<bool>? MonitorVisibilityChanged;

    public TeachingScreen()
    {
        var grid = new Grid();
        // Course viewer is the base; the monitor overlay stacks on top (collapsed until opened).
        grid.Children.Add(_coursePanel);
        grid.Children.Add(_monitorOverlay);
        Content = grid;

        _coursePanel.OpenMonitorRequested += (_, request) => OpenMonitor(request);
        _monitorOverlay.Closed += (_, _) => CloseMonitor();
    }

    private void OpenMonitor(EcgMonitorRequest? request = null)
    {
        // Triggered from an <ecg> embed: load its pathology and mirror its lead count + layout so
        // the monitor shows the same ECG the lecture figure does.
        if (request is not null && _monitorVm is not null && _rhythmVm is not null)
        {
            _rhythmVm.SelectRhythm(request.PathologyId);
            _monitorVm.SetSeriesCount(request.Leads.Count == 0 ? 12 : request.Leads.Count);
            _monitorVm.SetSeriesScheme(request.Scheme);
        }

        // The lecture WebView and the monitor's Win2D surface are both native airspace controls
        // that render above XAML; hide the course panel so only the monitor surface is live.
        _coursePanel.Visibility = Visibility.Collapsed;
        _monitorOverlay.Open();
        MonitorVisibilityChanged?.Invoke(this, true);
    }

    private void CloseMonitor()
    {
        _monitorOverlay.Visibility = Visibility.Collapsed;
        _coursePanel.Visibility = Visibility.Visible;
        _coursePanel.Refresh();
        MonitorVisibilityChanged?.Invoke(this, false);
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _lastCompareMode = monitorVm.MonitorMode.IsCompareMode;

        _coursePanel.Bind(appVm, appVm.CourseViewerViewModel, rhythmVm);
        _monitorOverlay.Bind(monitorVm, rhythmVm, appVm);

        monitorVm.PropertyChanged += OnMonitorChanged;
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorViewModel.MonitorMode) || _monitorVm is null) return;

        // Entering compare mode is an inherently visual, pane-tapping interaction — surface the
        // monitor overlay automatically so the user can see and edit the comparison panes.
        var compare = _monitorVm.MonitorMode.IsCompareMode;
        if (compare && !_lastCompareMode && !_monitorOverlay.IsOpen) OpenMonitor();
        _lastCompareMode = compare;
    }
}
