using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Teaching mode: the monitor with a left rhythm drawer (collapsible, pinnable). A School button
/// opens a full-screen <see cref="CourseViewerOverlay"/> (lecture WebView + Course/Lecture
/// drawers) over the monitor without disturbing it. Port of the Android <c>TeachingScreen</c>.
/// </summary>
public sealed class TeachingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly RhythmChoosingDrawer _rhythmDrawer = new();
    private readonly CourseViewerOverlay _courseOverlay = new();
    private readonly Button _courseButton = new()
    {
        Content = new FontIcon { Glyph = "" }, // "Education"/School glyph (Segoe MDL2)
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 8, 8, 0),
    };
    private readonly Grid _contentHost;
    private Grid _grid = null!;
    private RhythmViewModel? _rhythmVm;
    private CourseViewerViewModel? _courseVm;
    private AppViewModel? _appVm;

    /// <summary>Raised when a comparison pane is tapped, carrying the pane index.</summary>
    public event EventHandler<int>? PaneTapped
    {
        add => _monitor.PaneTapped += value;
        remove => _monitor.PaneTapped -= value;
    }

    public TeachingScreen()
    {
        _contentHost = new Grid();
        _monitor.Margin = new Thickness(24, 0, 0, 0);
        _contentHost.Children.Add(_monitor);
        _contentHost.Children.Add(_courseButton);

        // Column 0 sizes to the rhythm drawer; column 1 holds the content. Unpinned: content spans
        // both columns and the collapsed drawer/handle floats over it. Pinned: content is confined
        // to column 1 so the monitor lays out beside the open drawer (Android isDrawerFixed branch).
        _grid = new Grid();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(_contentHost, 0);
        Grid.SetColumnSpan(_contentHost, 2);
        _grid.Children.Add(_contentHost);

        _rhythmDrawer.HorizontalAlignment = HorizontalAlignment.Left;
        _rhythmDrawer.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_rhythmDrawer, 0);
        _grid.Children.Add(_rhythmDrawer);

        // Full-screen overlay spans both columns and sits on top; collapsed until opened.
        Grid.SetColumn(_courseOverlay, 0);
        Grid.SetColumnSpan(_courseOverlay, 2);
        _grid.Children.Add(_courseOverlay);

        _courseButton.Click += (_, _) =>
        {
            _monitor.Visibility = Visibility.Collapsed;
            _rhythmDrawer.Visibility = Visibility.Collapsed;
            _courseOverlay.Open();
        };
        _courseOverlay.Closed += (_, _) =>
        {
            _courseOverlay.Visibility = Visibility.Collapsed;
            _monitor.Visibility = Visibility.Visible;
            _rhythmDrawer.Visibility = Visibility.Visible;
        };

        Content = _grid;
    }

    /// <summary>
    /// Applies the pinned-drawer layout: pinned confines the content to column 1 (monitor beside
    /// the open drawer); unpinned lets the content span both columns with the drawer floating over.
    /// </summary>
    private void ApplyDrawerPin(bool pinned)
    {
        _rhythmDrawer.SetPinned(pinned);
        if (pinned)
        {
            Grid.SetColumn(_contentHost, 1);
            Grid.SetColumnSpan(_contentHost, 1);
            _monitor.Margin = new Thickness(0);
        }
        else
        {
            Grid.SetColumn(_contentHost, 0);
            Grid.SetColumnSpan(_contentHost, 2);
            _monitor.Margin = new Thickness(24, 0, 0, 0);
        }
    }

    public void Initialize(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _courseVm = appVm.CourseViewerViewModel;

        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
        _rhythmDrawer.DisplayLanguage = appVm.SelectedLanguage;
        _rhythmDrawer.SetRhythms(rhythmVm.Rhythms);
        _rhythmDrawer.SelectedId = rhythmVm.SelectedRhythm?.Id;
        _rhythmDrawer.RhythmSelected += (_, entry) => rhythmVm.SelectRhythm(entry.Id);

        _courseOverlay.Bind(appVm, _courseVm);

        _rhythmDrawer.PinnedChanged += (_, pinned) =>
        {
            appVm.SetDrawerFixed(pinned);
            ApplyDrawerPin(pinned);
        };
        ApplyDrawerPin(appVm.IsDrawerFixed);

        rhythmVm.PropertyChanged += OnRhythmChanged;
        appVm.PropertyChanged += OnAppChanged;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _monitor.DisplayLanguage = _appVm.SelectedLanguage;
            _rhythmDrawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
        {
            _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
        }
        else if (e.PropertyName == nameof(RhythmViewModel.SelectedRhythm))
        {
            _rhythmDrawer.SelectedId = _rhythmVm.SelectedRhythm?.Id;
        }
    }
}
