using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Teaching mode. The course selector drives the main view: "All rhythms" shows the monitor (the
/// rhythms window) as a standalone mode, while selecting a course shows that course's lectures in
/// the <see cref="CourseViewerPanel"/>. From a lecture the monitor can still be popped over the
/// course (the panel's heart button or an inline <c>&lt;ecg&gt;</c> embed). Entering Teaching
/// defaults to "All rhythms" (the monitor) — a deliberate Windows divergence from Android.
/// </summary>
public sealed class TeachingScreen : UserControl
{
    private readonly CourseViewerPanel _coursePanel = new();
    private readonly MonitorViewerOverlay _monitorOverlay = new();

    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private bool _lastCompareMode;

    /// <summary>Raised when a comparison pane is tapped, carrying the pane index.</summary>
    public event EventHandler<int>? PaneTapped
    {
        add => _monitorOverlay.PaneTapped += value;
        remove => _monitorOverlay.PaneTapped -= value;
    }

    /// <summary>Raised when the monitor is shown (true) or hidden (false), so the host can
    /// reveal/hide the bottom monitor control panel (hidden while a course's lectures are showing).</summary>
    public event EventHandler<bool>? MonitorVisibilityChanged;

    public TeachingScreen()
    {
        var grid = new Grid();
        // Course viewer is the base; the monitor stacks on top (shown for "All rhythms" or popped
        // over a course).
        grid.Children.Add(_coursePanel);
        grid.Children.Add(_monitorOverlay);
        Content = grid;

        _coursePanel.OpenMonitorRequested += (_, request) => OpenMonitor(request);
        _monitorOverlay.Closed += (_, _) => CloseMonitor();
    }

    /// <summary>True when no course is selected ("All rhythms") — the monitor is the main view.</summary>
    private bool IsAllRhythms => _appVm is not null && _appVm.SelectedCourseId is null;

    private void OpenMonitor(EcgMonitorRequest? request = null)
    {
        // Triggered from an <ecg> embed: load its pathology and mirror its lead count + layout so
        // the monitor shows the same ECG the lecture figure does.
        if (request is not null && _monitorVm is not null && _rhythmVm is not null)
        {
            _rhythmVm.SelectRhythm(request.PathologyId);
            _monitorVm.SetLeadSelection(request.Leads); // exact handpicked leads (empty ⇒ all 12)
            _monitorVm.SetSeriesScheme(request.Scheme);
        }

        // The lecture WebView and the monitor's Win2D surface are both native airspace controls
        // that render above XAML; hide the course panel so only the monitor surface is live.
        _coursePanel.Visibility = Visibility.Collapsed;
        // "All rhythms" → the monitor is the standalone main view (no close button); over a course
        // → it's a dismissible pop-over.
        _monitorOverlay.SetCloseButtonVisible(!IsAllRhythms);
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
        _appVm = appVm;
        _lastCompareMode = monitorVm.MonitorMode.IsCompareMode;

        _coursePanel.Bind(appVm, appVm.CourseViewerViewModel);
        _monitorOverlay.Bind(monitorVm, rhythmVm, appVm);

        monitorVm.PropertyChanged += OnMonitorChanged;
        appVm.PropertyChanged += OnAppChanged;

        ApplyCourseMode();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedCourseId)) ApplyCourseMode();
    }

    /// <summary>Switches the main view to match the course selector: the monitor for "All rhythms",
    /// the course's lectures otherwise.</summary>
    private void ApplyCourseMode()
    {
        if (IsAllRhythms) OpenMonitor();
        else CloseMonitor();
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorViewModel.MonitorMode) || _monitorVm is null) return;

        // Entering compare mode is an inherently visual, pane-tapping interaction — surface the
        // monitor automatically so the user can see and edit the comparison panes.
        var compare = _monitorVm.MonitorMode.IsCompareMode;
        if (compare && !_lastCompareMode && !_monitorOverlay.IsOpen) OpenMonitor();
        _lastCompareMode = compare;
    }
}
