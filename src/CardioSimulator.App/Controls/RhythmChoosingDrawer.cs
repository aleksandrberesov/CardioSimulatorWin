using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Collapsible left drawer wrapping a <see cref="RhythmChoosingPanel"/> behind a toggle
/// handle. Port of the Android <c>RhythmChoosingDrawer</c>.
/// </summary>
public sealed class RhythmChoosingDrawer : UserControl
{
    private readonly RhythmChoosingPanel _panel = new();
    private readonly Border _panelHost;
    private readonly Grid _handle;
    private readonly Border _handleBg;
    private bool _isExpanded;
    private bool _pinned;

    public event EventHandler<PathologyEntry>? RhythmSelected;

    /// <summary>Raised when the in-panel "Fix drawer" checkbox toggles.</summary>
    public event EventHandler<bool>? PinnedChanged;

    private readonly TextBlock _label;

    public RhythmChoosingDrawer()
    {
        _panelHost = new Border
        {
            // Width matches the top mode block (TopControlPanel ModeTab) so the panel's right edge
            // lines up with the vertical divider running down the screen.
            Width = 280,
            Background = Theming.AppTheme.PanelBackground,
            BorderBrush = Theming.AppTheme.ControlBorder,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _panel,
            Visibility = Visibility.Collapsed,
        };
        _panel.RhythmSelected += (_, entry) => RhythmSelected?.Invoke(this, entry);
        _panel.PinnedChanged += (_, pinned) => PinnedChanged?.Invoke(this, pinned);

        // Rotated vertical text label, matching the Android SideDrawer handle.
        //
        // RenderTransform does NOT affect layout: the label is measured/arranged HORIZONTALLY (at its
        // full word width) and only then rotated into the 24px-wide vertical strip. A fixed-width
        // parent therefore clips the pre-rotation text back to ~24px — showing only the middle of the
        // word ("…ИТМ…"), the reported "Ритмы обрезается" bug. Two things clip it: the framework's
        // layout clip (any parent narrower than the child clips the child to its arrange slot) and the
        // rounded Border's own corner-radius clip.
        //
        // Fix: host the label in a Canvas — the one panel that arranges a child at its *own* desired
        // size, so no layout clip is ever applied — and lay that Canvas *over* the handle background
        // as a sibling (not nested inside the rounded Border, whose clip would re-trim it). The label
        // is free to overflow horizontally; the -90° rotation folds it back into the centred strip.
        const double handleWidth = 24;
        const double handleLength = 96;

        _label = new TextBlock
        {
            Text = AppStrings.EditorRhythmsTitle,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theming.AppTheme.TextPrimary,
            Width = handleLength,           // horizontal room for the full word (pre-rotation)
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = -90 },
        };

        // The Canvas lets the label lay out unclipped; it overflows the Canvas horizontally by design,
        // and the -90° rotation folds it back into the centred strip. Give the Canvas an EXPLICIT size
        // matching the handle: a Canvas's own desired size is 0×0, so without this its coordinate space
        // collapses and the label parks at the top-left corner, spilling outside the handle.
        var labelLayer = new Canvas
        {
            Width = handleWidth,
            Height = handleLength,
            IsHitTestVisible = false,
        };
        labelLayer.Children.Add(_label);
        // Horizontal placement is deterministic from the known widths (the label is a fixed
        // handleLength wide); vertical placement uses the label's measured height so the word sits dead
        // centre. Re-run when the label's height becomes known (it is measured on a later pass).
        void CentreLabel()
        {
            Canvas.SetLeft(_label, (handleWidth - handleLength) / 2);
            Canvas.SetTop(_label, (handleLength - _label.ActualHeight) / 2);
        }
        _label.SizeChanged += (_, _) => CentreLabel();
        CentreLabel();

        _handleBg = new Border
        {
            Background = Theming.AppTheme.ControlFill,
            BorderBrush = Theming.AppTheme.ControlBorder,
            BorderThickness = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
        };

        _handle = new Grid
        {
            Width = handleWidth,
            MinHeight = handleLength,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _handleBg, labelLayer },
        };
        _handle.Tapped += (_, _) => ToggleExpanded();

        Loaded += (_, _) => Theming.AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        row.Children.Add(_panelHost);
        row.Children.Add(_handle);
        Content = row;
    }

    private void OnThemeChanged()
    {
        _panelHost.Background = Theming.AppTheme.PanelBackground;
        _panelHost.BorderBrush = Theming.AppTheme.ControlBorder;
        _handleBg.Background = Theming.AppTheme.ControlFill;
        _handleBg.BorderBrush = Theming.AppTheme.ControlBorder;
        _label.Foreground = Theming.AppTheme.TextPrimary;
    }

    /// <summary>
    /// Pinned: the panel stays open and the toggle handle is hidden, so the host can lay the
    /// drawer out inline beside the monitor (Android's <c>isDrawerFixed</c> branch). Unpinned
    /// restores the collapsible handle.
    /// </summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        _panel.Pinned = pinned;
        _handle.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        _panelHost.Visibility = pinned || _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    public DomainLanguage DisplayLanguage
    {
        get => _panel.DisplayLanguage;
        set => _panel.DisplayLanguage = value;
    }

    /// <summary>Shows large up/down page-scroll buttons in the list's bottom-right corner.</summary>
    public bool ShowScrollButtons
    {
        get => _panel.ShowScrollButtons;
        set => _panel.ShowScrollButtons = value;
    }

    public string? SelectedId
    {
        get => _panel.SelectedId;
        set => _panel.SelectedId = value;
    }

    /// <summary>
    /// Whether filtering the list may auto-select the first remaining match (see
    /// <see cref="RhythmChoosingPanel.AutoSelectOnFilter"/>). The Constructor turns this off so it
    /// never silently switches the pathology being edited.
    /// </summary>
    public bool AutoSelectOnFilter
    {
        get => _panel.AutoSelectOnFilter;
        set => _panel.AutoSelectOnFilter = value;
    }

    /// <summary>
    /// The clinical-cases vs plain-rhythms filter (see <see cref="RhythmChoosingPanel.ClinicalMode"/>).
    /// The Constructor sets this to follow the edited pathology so it shows in its correct list.
    /// </summary>
    public bool ClinicalMode
    {
        get => _panel.ClinicalMode;
        set => _panel.ClinicalMode = value;
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms) => _panel.SetRhythms(rhythms);

    private void ToggleExpanded()
    {
        if (_pinned) return; // pinned drawer is always open
        _isExpanded = !_isExpanded;
        _panelHost.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
