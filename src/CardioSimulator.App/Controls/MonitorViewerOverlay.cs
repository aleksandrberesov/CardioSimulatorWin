using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Full-screen monitor overlay opened from the Teaching screen's monitor button. Hosts the Win2D
/// <see cref="MonitorView"/> plus a left-edge collapsible/pinnable rhythm drawer, with a top bar
/// (title + close). Mirrors <see cref="CourseViewerOverlay"/>'s structure but for the monitor —
/// this is the Windows inversion where the course viewer is the base and the monitor is the
/// pop-over (Android keeps the monitor as the base and the course viewer as the overlay).
/// </summary>
public sealed class MonitorViewerOverlay : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly RhythmChoosingDrawer _rhythmDrawer = new();
    private readonly TextBlock _title = new()
    {
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Grid _contentGrid = null!;
    private Grid _topBar = null!;
    private Button _close = null!;

    // Vertical line between the rhythm panel and the monitor, drawn only when the drawer is pinned
    // (open beside the monitor). Continues the divider that starts in the top mode bar.
    private readonly Border _divider = new()
    {
        Width = 1,
        Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xCC, G = 0xCC, B = 0xCC }),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Stretch,
        Visibility = Visibility.Collapsed,
    };

    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;

    /// <summary>Raised when the overlay's close button is tapped.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised when a comparison pane is tapped, carrying the pane index.</summary>
    public event EventHandler<int>? PaneTapped
    {
        add => _monitor.PaneTapped += value;
        remove => _monitor.PaneTapped -= value;
    }

    public MonitorViewerOverlay()
    {
        Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xFA, G = 0xFA, B = 0xFA });
        Visibility = Visibility.Collapsed;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: title + close.
        _topBar = new Grid { Height = 56, Padding = new Thickness(16, 0, 8, 0), Background = new SolidColorBrush(Colors.WhiteSmoke) };
        _topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        _topBar.Children.Add(_title);
        _close = new Button { Content = new SymbolIcon(Symbol.Cancel), VerticalAlignment = VerticalAlignment.Center };
        _close.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_close, 1);
        _topBar.Children.Add(_close);
        Grid.SetRow(_topBar, 0);
        root.Children.Add(_topBar);

        // Content: column 0 sizes to the rhythm drawer; column 1 holds the monitor. Unpinned: the
        // monitor spans both columns and the collapsed drawer/handle floats over it. Pinned: the
        // monitor is confined to column 1 so it lays out beside the open drawer (Android
        // isDrawerFixed branch).
        _contentGrid = new Grid();
        _contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _monitor.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(_monitor, 0);
        Grid.SetColumnSpan(_monitor, 2);
        _contentGrid.Children.Add(_monitor);

        _rhythmDrawer.HorizontalAlignment = HorizontalAlignment.Left;
        _rhythmDrawer.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_rhythmDrawer, 0);
        _contentGrid.Children.Add(_rhythmDrawer);

        // Divider sits at the right edge of the rhythm-panel column (the panel/monitor boundary).
        // It lives on the panel side rather than over the monitor: the monitor is a native Win2D
        // surface that renders above XAML siblings, so a line over it would be occluded.
        Grid.SetColumn(_divider, 0);
        _contentGrid.Children.Add(_divider);

        Grid.SetRow(_contentGrid, 1);
        root.Children.Add(_contentGrid);

        Content = root;
    }

    /// <summary>
    /// Applies the pinned-drawer layout: pinned confines the monitor to column 1 (monitor beside
    /// the open drawer); unpinned lets the monitor span both columns with the drawer floating over.
    /// </summary>
    private void ApplyDrawerPin(bool pinned)
    {
        _rhythmDrawer.SetPinned(pinned);
        // The divider only makes sense when the panel is pinned open beside the monitor; when
        // unpinned the panel floats over the monitor, so there is no fixed boundary to mark.
        _divider.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        if (pinned)
        {
            Grid.SetColumn(_monitor, 1);
            Grid.SetColumnSpan(_monitor, 1);
            _monitor.Margin = new Thickness(0);
        }
        else
        {
            Grid.SetColumn(_monitor, 0);
            Grid.SetColumnSpan(_monitor, 2);
            _monitor.Margin = new Thickness(24, 0, 0, 0);
        }
    }

    public void Bind(MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;

        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
        _rhythmDrawer.DisplayLanguage = appVm.SelectedLanguage;
        _rhythmDrawer.SetRhythms(rhythmVm.Rhythms);
        _rhythmDrawer.SelectedId = rhythmVm.SelectedRhythm?.Id;
        _rhythmDrawer.RhythmSelected += (_, entry) => rhythmVm.SelectRhythm(entry.Id);

        _rhythmDrawer.PinnedChanged += (_, pinned) =>
        {
            appVm.SetDrawerFixed(pinned);
            ApplyDrawerPin(pinned);
        };
        ApplyDrawerPin(appVm.IsDrawerFixed);

        monitorVm.PropertyChanged += OnMonitorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        appVm.PropertyChanged += OnAppChanged;
        UpdateTitle();
    }

    /// <summary>True while the monitor overlay is on screen.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows or hides the close button: hidden when the monitor is the standalone
    /// "All rhythms" main view, shown when it's a pop-over the user can dismiss back to a course.
    /// In the standalone view the whole title bar is hidden too, so the monitor + rhythm panel fill
    /// the full height and the vertical divider runs unbroken (the rhythm name already shows in the
    /// top mode bar). The course pop-over keeps its title bar for the close button.</summary>
    public void SetCloseButtonVisible(bool visible)
    {
        _close.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _topBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _monitor.DisplayLanguage = _appVm.SelectedLanguage;
            _rhythmDrawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
            UpdateTitle();
        }
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode)) UpdateTitle();
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
            UpdateTitle();
        }
    }

    /// <summary>
    /// Title mirrors the Android monitor header: "Compare mode" while comparing, otherwise the
    /// selected rhythm's localized name, falling back to the mode name when nothing is selected.
    /// </summary>
    private void UpdateTitle()
    {
        if (_appVm is null) { _title.Text = string.Empty; return; }
        if (_monitorVm?.MonitorMode.IsCompareMode == true)
        {
            _title.Text = AppStrings.CompareModeTitle;
            return;
        }
        var rhythm = _rhythmVm?.SelectedRhythm;
        _title.Text = rhythm is null
            ? AppStrings.ModeName(OperatingMode.Teaching)
            : (_appVm.SelectedLanguage == DomainLanguage.RU ? rhythm.NameRu ?? rhythm.TitleEn : rhythm.TitleEn);
    }

    /// <summary>Shows the overlay over the course viewer.</summary>
    public void Open()
    {
        UpdateTitle();
        Visibility = Visibility.Visible;
    }
}
