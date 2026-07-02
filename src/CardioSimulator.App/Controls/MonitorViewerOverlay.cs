using System.ComponentModel;
using System.Linq;
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

    // "Rhythm info" button (top-right of the monitor) shown only in the standalone "All rhythms"
    // view. Tapping it opens _infoScreen — a full-monitor takeover (not a small corner card) hosting
    // the composed details (_infoContent) under a header with a close button.
    private Button _info = null!;
    private Grid _infoScreen = null!;
    private readonly TextBlock _infoHeaderTitle = new()
    {
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel _infoContent = new() { Spacing = 6 };

    // Floating "grid scale" card showing current sweep speed and amplitude calibration.
    private readonly Border _gridScaleCard = new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 10, 6),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(230, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 0, 0)),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBlock _gridScaleText = new()
    {
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1B, 0x24, 0x30)),
        VerticalAlignment = VerticalAlignment.Center,
    };

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

    /// <summary>Toggles the monitor's ruler/caliper measurement tool.</summary>
    public bool RulerActive
    {
        get => _monitor.RulerActive;
        set => _monitor.RulerActive = value;
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

        // Rhythm-info button at the monitor's top-right corner. Shown only in the standalone "All
        // rhythms" view (where the title bar is hidden); tapping it opens a full-monitor info screen
        // (see _infoScreen) describing the selected rhythm — the composed pathology card (name, lead
        // count, ECG markers) that lived in the course panel before "All rhythms" switched to the
        // monitor. Floating XAML over the Win2D monitor is fine here (the SQI quality card does the
        // same; it sits at the bottom-right, clear of this top-right button).
        _info = new Button
        {
            Content = new FontIcon { Glyph = "" }, // Segoe MDL2 "Education" (graduation cap) glyph
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12),
            Visibility = Visibility.Collapsed,
        };
        _info.Click += (_, _) => ShowInfoScreen(true);
        ToolTipService.SetToolTip(_info, AppStrings.RhythmInfoTooltip);
        Grid.SetColumn(_info, 0);
        Grid.SetColumnSpan(_info, 2);
        _contentGrid.Children.Add(_info);

        _gridScaleCard.Child = _gridScaleText;
        Grid.SetColumn(_gridScaleCard, 0);
        Grid.SetColumnSpan(_gridScaleCard, 2);
        _contentGrid.Children.Add(_gridScaleCard);

        // Full-monitor rhythm-info screen: spans the whole content area and is opaque, so when shown
        // it fully takes over the monitor space (covering the monitor, drawer, and floating cards).
        // Added last so it sits above every other content-grid child.
        _infoScreen = BuildInfoScreen();
        Grid.SetColumn(_infoScreen, 0);
        Grid.SetColumnSpan(_infoScreen, 2);
        _contentGrid.Children.Add(_infoScreen);

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

            Grid.SetColumn(_gridScaleCard, 1);
            Grid.SetColumnSpan(_gridScaleCard, 1);
            _gridScaleCard.Margin = new Thickness(12);
        }
        else
        {
            Grid.SetColumn(_monitor, 0);
            Grid.SetColumnSpan(_monitor, 2);
            _monitor.Margin = new Thickness(24, 0, 0, 0);

            Grid.SetColumn(_gridScaleCard, 0);
            Grid.SetColumnSpan(_gridScaleCard, 2);
            _gridScaleCard.Margin = new Thickness(36, 12, 12, 12);
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
        UpdateGridScaleText();
    }

    /// <summary>True while the monitor overlay is on screen.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows or hides the close button: hidden when the monitor is the standalone
    /// "All rhythms" main view, shown when it's a pop-over the user can dismiss back to a course.
    /// In the standalone view the whole title bar is hidden too, so the monitor + rhythm panel fill
    /// the full height and the vertical divider runs unbroken (the rhythm name already shows in the
    /// top mode bar). The course pop-over keeps its title bar for the close button.
    /// The floating rhythm-info button is the standalone view's counterpart of that hidden title
    /// bar, so it appears exactly when the close button is hidden.</summary>
    public void SetCloseButtonVisible(bool visible)
    {
        _close.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _topBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _info.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        // Don't leave the info screen lingering across a mode switch (e.g. all-rhythms → course).
        ShowInfoScreen(false);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _monitor.DisplayLanguage = _appVm.SelectedLanguage;
            _rhythmDrawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _rhythmDrawer.SetRhythms(_rhythmVm.Rhythms);
            ToolTipService.SetToolTip(_info, AppStrings.RhythmInfoTooltip);
            _infoHeaderTitle.Text = AppStrings.RhythmInfoTitle;
            if (_infoScreen.Visibility == Visibility.Visible) BuildInfoContent();
            UpdateTitle();
            UpdateGridScaleText();
        }
    }

    /// <summary>
    /// Builds the full-monitor rhythm-info screen: a header (localized title + close button) over a
    /// scrollable details panel (<see cref="_infoContent"/>). Opaque so the Win2D monitor behind it
    /// is hidden when shown.
    /// </summary>
    private Grid BuildInfoScreen()
    {
        var screen = new Grid
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0xFA, G = 0xFA, B = 0xFA }),
            Visibility = Visibility.Collapsed,
        };
        screen.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        screen.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Height = 56, Padding = new Thickness(16, 0, 8, 0), Background = new SolidColorBrush(Colors.WhiteSmoke) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _infoHeaderTitle.Text = AppStrings.RhythmInfoTitle;
        Grid.SetColumn(_infoHeaderTitle, 0);
        header.Children.Add(_infoHeaderTitle);
        var closeInfo = new Button { Content = new SymbolIcon(Symbol.Cancel), VerticalAlignment = VerticalAlignment.Center };
        closeInfo.Click += (_, _) => ShowInfoScreen(false);
        Grid.SetColumn(closeInfo, 1);
        header.Children.Add(closeInfo);
        Grid.SetRow(header, 0);
        screen.Children.Add(header);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(40, 32, 40, 32),
        };
        scroll.Content = _infoContent;
        Grid.SetRow(scroll, 1);
        screen.Children.Add(scroll);

        return screen;
    }

    /// <summary>Opens (rebuilding its content first) or closes the full-monitor rhythm-info screen.</summary>
    private void ShowInfoScreen(bool show)
    {
        if (show) BuildInfoContent();
        _infoScreen.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the rhythm-info screen from the selected pathology's manifest data and significant
    /// points — the composed card (localized name, lead count, distinct ECG markers) that used to
    /// live in the course panel. Rebuilt each time the screen opens, so it always reflects the
    /// current rhythm and language.
    /// </summary>
    private void BuildInfoContent()
    {
        _infoContent.Children.Clear();

        var entry = _rhythmVm?.SelectedRhythm;
        if (entry is null)
        {
            _infoContent.Children.Add(new TextBlock
            {
                Text = AppStrings.ModeName(OperatingMode.Teaching),
                FontSize = 24,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var ru = _appVm?.SelectedLanguage == DomainLanguage.RU;
        var primary = ru ? entry.NameRu ?? entry.TitleEn : entry.TitleEn;
        var secondary = ru ? entry.TitleEn : entry.NameRu;

        _infoContent.Children.Add(new TextBlock
        {
            Text = primary,
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(secondary) && secondary != primary)
        {
            _infoContent.Children.Add(new TextBlock
            {
                Text = secondary,
                FontSize = 18,
                Foreground = new SolidColorBrush(Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        _infoContent.Children.Add(new TextBlock
        {
            Text = $"{AppStrings.PathologyLeadsLabel}: {entry.LeadsCount}",
            FontSize = 18,
            Margin = new Thickness(0, 16, 0, 0),
        });
        var markers = MarkerSummary();
        if (!string.IsNullOrEmpty(markers))
        {
            _infoContent.Children.Add(new TextBlock
            {
                Text = $"{AppStrings.PathologyMarkersLabel}: {markers}",
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (!string.IsNullOrWhiteSpace(_rhythmVm?.Description))
        {
            _infoContent.Children.Add(new TextBlock
            {
                Text = AppStrings.PathologyDescriptionLabel + ":",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 0),
            });
            _infoContent.Children.Add(new TextBlock
            {
                Text = _rhythmVm.Description,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    /// <summary>Distinct ECG significant-point labels for the selected rhythm, in complex order
    /// (plain text — the <c>&lt;sub&gt;</c> tags in the source labels are stripped).</summary>
    private string MarkerSummary()
    {
        var points = _rhythmVm?.SignificantPoints;
        if (points is null || points.Count == 0) return string.Empty;
        var labels = points
            .Select(p => p.Type)
            .Distinct()
            .OrderBy(t => (int)t)
            .Select(t => t.Label().Replace("<sub>", string.Empty).Replace("</sub>", string.Empty));
        return string.Join(", ", labels);
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode))
        {
            UpdateTitle();
            UpdateGridScaleText();
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

    private void UpdateGridScaleText()
    {
        if (_monitorVm is null) return;
        var mode = _monitorVm.MonitorMode;
        var speedText = mode.Speed % 1 == 0 ? ((int)mode.Speed).ToString() : mode.Speed.ToString("0.#");
        var gain = mode.Calibration.GainMmPerMv;
        var gainText = gain % 1 == 0 ? ((int)gain).ToString() : gain.ToString("0.#");

        _gridScaleText.Text = string.Format(AppStrings.MonitorGridScaleFormat, speedText, gainText);
    }

    /// <summary>Shows the overlay over the course viewer.</summary>
    public void Open()
    {
        UpdateTitle();
        Visibility = Visibility.Visible;
    }
}
