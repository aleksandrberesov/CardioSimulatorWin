using System.Collections.Generic;
using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The monitor's bottom control row (Teaching mode). Count / scheme / speed / scale dropdowns,
/// then the three windowed/overlay options — Electrodes (opens a window), Artifacts (dropdown),
/// 3D heart (opens a window) — then the pQRSt impulse-label toggle, EOS / Tips, and the
/// ruler / Compare / start-stop controls. Drives a <see cref="MonitorViewModel"/>. All labels are
/// routed through <see cref="AppStrings"/>.
/// </summary>
public sealed partial class MonitorControlPanel : UserControl
{
    private static readonly string GlyphPlay = char.ConvertFromUtf32(0xE768);
    private static readonly string GlyphStop = char.ConvertFromUtf32(0xE71A);

    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    /// <summary>Alert-red fill for the Electrodes tab when a hookup fault (swapped/displaced) is active.</summary>
    private static readonly SolidColorBrush ElectrodeFaultFill =
        new(new Windows.UI.Color { A = 0xFF, R = 0xD3, G = 0x3A, B = 0x2F });

    private MonitorViewModel? _viewModel;
    private bool _pqrstActive;
    private bool _rulerActive;
    private EcgArtifacts _artifacts = EcgArtifacts.None;

    /// <summary>Raised when start/stop is toggled, carrying the new running state.</summary>
    public event EventHandler<bool>? StartStopClick;

    /// <summary>Raised when the Compare button is clicked (Teaching mode multi-rhythm overlay).</summary>
    public event EventHandler? CompareClick;

    /// <summary>Raised when the Electrodes button is clicked (opens the electrode-placement window).</summary>
    public event EventHandler? ElectrodesClick;

    /// <summary>Raised when the 3D button is clicked (opens the rotatable 3D-heart window).</summary>
    public event EventHandler? Heart3DClick;

    /// <summary>Raised when the EOS button is clicked (opens the electrical-axis window).</summary>
    public event EventHandler? EosClick;

    /// <summary>Raised when the Tips button is clicked (opens the annotation-overlay palette window).</summary>
    public event EventHandler? TipsClick;

    /// <summary>Raised when pQRSt is toggled, carrying whether impulse labels are now shown.</summary>
    public event EventHandler<bool>? PqrstToggled;

    /// <summary>Raised when the active artifact set changes (multi-select Artifacts menu).</summary>
    public event EventHandler<EcgArtifacts>? ArtifactSelected;

    /// <summary>Raised when the ruler/caliper tool is toggled, carrying whether it is now active.</summary>
    public event EventHandler<bool>? RulerToggled;

    public MonitorControlPanel()
    {
        InitializeComponent();
        SetStaticLabels();
    }

    private void SetStaticLabels()
    {
        ElectrodesTab.Text = AppStrings.MonitorElectrodes;
        ArtifactsTab.Text = AppStrings.MonitorArtifacts;
        FiltersTab.Text = AppStrings.MonitorFilters;
        Heart3DText.Text = AppStrings.Monitor3D;
        PqrstText.Text = AppStrings.MonitorPqrst;
        EosText.Text = AppStrings.MonitorEos;
        TipsTab.Text = AppStrings.MonitorTips;
        SpeedTab.SubText = AppStrings.MonitorSpeedUnit;
        CompareTab.Text = AppStrings.CompareButton;
        ToolTipService.SetToolTip(RulerButton, AppStrings.MonitorRuler);
        ApplyRulerVisual();
    }

    /// <summary>Clears the ruler toggle visual without raising <see cref="RulerToggled"/> (used when
    /// the monitor is dismissed so the button doesn't stay lit over a hidden surface).</summary>
    public void ResetRuler()
    {
        _rulerActive = false;
        ApplyRulerVisual();
    }

    private void OnCompareClick(object? sender, EventArgs e) => CompareClick?.Invoke(this, EventArgs.Empty);

    private void OnRulerTapped(object sender, TappedRoutedEventArgs e)
    {
        _rulerActive = !_rulerActive;
        ApplyRulerVisual();
        RulerToggled?.Invoke(this, _rulerActive);
    }

    private void OnRulerPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_rulerActive) RulerButton.Background = AppTheme.HoverFill;
    }

    private void OnRulerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_rulerActive) RulerButton.Background = Transparent;
    }

    private void ApplyRulerVisual()
    {
        RulerButton.Background = _rulerActive ? AppTheme.Accent : Transparent;
        RulerIcon.Stroke = _rulerActive ? AppTheme.OnAccent : AppTheme.TextPrimary;
    }
    private void OnElectrodesClick(object? sender, EventArgs e) => ElectrodesClick?.Invoke(this, EventArgs.Empty);
    private void OnTipsClick(object? sender, EventArgs e) => TipsClick?.Invoke(this, EventArgs.Empty);

    private void OnHeart3DTapped(object sender, TappedRoutedEventArgs e) => Heart3DClick?.Invoke(this, EventArgs.Empty);
    private void OnHeart3DPointerEntered(object sender, PointerRoutedEventArgs e) => Heart3DButton.Background = AppTheme.HoverFill;
    private void OnHeart3DPointerExited(object sender, PointerRoutedEventArgs e) => Heart3DButton.Background = Transparent;

    private void OnEosTapped(object sender, TappedRoutedEventArgs e) => EosClick?.Invoke(this, EventArgs.Empty);
    private void OnEosPointerEntered(object sender, PointerRoutedEventArgs e) => EosButton.Background = AppTheme.HoverFill;
    private void OnEosPointerExited(object sender, PointerRoutedEventArgs e) => EosButton.Background = Transparent;

    public void Bind(MonitorViewModel viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _pqrstActive = viewModel.MonitorMode.ShowImpulseLabels;
        ApplyPqrstVisual();
        UpdateTexts();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The "user has chosen an electrode hookup" flag is a separate observable, so refresh the
        // Electrodes highlight on its change too (it doesn't ride on MonitorMode).
        if (e.PropertyName == nameof(MonitorViewModel.ElectrodeStateUserSet))
        {
            ApplyElectrodesVisual();
            return;
        }
        if (e.PropertyName != nameof(MonitorViewModel.MonitorMode)) return;
        UpdateTexts();
        // Keep the toggle visual in sync if the flag is changed elsewhere.
        if (_viewModel is not null && _viewModel.MonitorMode.ShowImpulseLabels != _pqrstActive)
        {
            _pqrstActive = _viewModel.MonitorMode.ShowImpulseLabels;
            ApplyPqrstVisual();
        }
    }

    private void UpdateTexts()
    {
        if (_viewModel is null) return;
        var mode = _viewModel.MonitorMode;
        CountTab.Text = AppStrings.MonitorCountFormat(mode.Count);
        SchemeTab.Text = mode.SeriesScheme switch
        {
            SeriesScheme.OneColumn => AppStrings.MonitorColumnsOneShort,
            SeriesScheme.TwoColumn => AppStrings.MonitorColumnsTwoShort,
            SeriesScheme.Grid => AppStrings.MonitorColumnsGridShort,
            _ => string.Empty,
        };
        SpeedTab.Text = mode.Speed % 1 == 0 ? ((int)mode.Speed).ToString() : mode.Speed.ToString("0.#");
        ScaleTab.Text = $"{(int)(mode.Scale * 100)}%";
        FiltersTab.Text = mode.FilterType switch
        {
            EcgFilterType.None => AppStrings.MonitorFilterNone,
            EcgFilterType.Lowpass => AppStrings.MonitorFilterLp,
            EcgFilterType.Highpass => AppStrings.MonitorFilterHp,
            EcgFilterType.Bandpass => AppStrings.MonitorFilterBp,
            _ => AppStrings.MonitorFilterNone
        };
        _artifacts = mode.Artifacts;
        ArtifactsTab.IsActive = _artifacts != EcgArtifacts.None;
        ArtifactsTab.Text = ArtifactsLabel(_artifacts);
        CompareTab.IsActive = mode.IsCompareMode;
        StartStopTab.Glyph = mode.IsRunning ? GlyphStop : GlyphPlay;
        ApplyElectrodesVisual();
    }

    /// <summary>
    /// Tri-state highlight for the Electrodes tab: neutral until the student has used the window,
    /// then green for a confirmed-OK hookup and red while a fault (swapped/displaced) is active.
    /// </summary>
    private void ApplyElectrodesVisual()
    {
        if (_viewModel is null) return;
        if (!_viewModel.ElectrodeStateUserSet)
        {
            // Never used → leave the tab in its original (inactive) look.
            ElectrodesTab.IsActive = false;
            return;
        }
        var ok = _viewModel.MonitorMode.ElectrodeState == ElectrodeState.Ok;
        ElectrodesTab.ActiveBrush = ok ? AppTheme.Accent : ElectrodeFaultFill;
        ElectrodesTab.IsActive = true;
    }

    private static string ArtifactsLabel(EcgArtifacts artifacts)
    {
        if (artifacts == EcgArtifacts.None) return AppStrings.MonitorArtifacts;
        int count = System.Numerics.BitOperations.PopCount((uint)artifacts);
        return $"{AppStrings.MonitorArtifacts} ({count})";
    }

    // ── pQRSt impulse-label toggle ──────────────────────────────────────────

    private void OnPqrstTapped(object sender, TappedRoutedEventArgs e)
    {
        _pqrstActive = !_pqrstActive;
        ApplyPqrstVisual();
        _viewModel?.SetShowImpulseLabels(_pqrstActive);
        PqrstToggled?.Invoke(this, _pqrstActive);
    }

    private void ApplyPqrstVisual()
    {
        PqrstButton.Background = _pqrstActive ? AppTheme.Accent : Transparent;
        PqrstText.Foreground = _pqrstActive ? AppTheme.OnAccent : AppTheme.TextPrimary;
    }

    private void OnPqrstPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_pqrstActive) PqrstButton.Background = AppTheme.HoverFill;
    }

    private void OnPqrstPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_pqrstActive) PqrstButton.Background = Transparent;
    }

    // ── Menu header with info sign ──────────────────────────────────────────

    // A flyout title row: bold label plus a circled-info "(!)" sign whose tooltip explains how the
    // menu works. Shared by the Artifacts and Filters dropdowns.
    private static UIElement BuildMenuHeader(string title, string explanation)
    {
        var label = new TextBlock
        {
            Text = title,
            Foreground = AppTheme.TextPrimary,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(4, 0, 4, 6),
        };
        row.Children.Add(label);
        row.Children.Add(BuildInfoSign(explanation));
        return row;
    }

    // A small circled-info icon; hovering it surfaces a wrapped tooltip describing how the menu works.
    private static UIElement BuildInfoSign(string explanation)
    {
        var icon = new FontIcon
        {
            Glyph = "", // Info (circled "i")
            FontSize = 14,
            Foreground = AppTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tip = new ToolTip
        {
            Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 },
        };
        ToolTipService.SetToolTip(icon, tip);
        return icon;
    }

    // ── Artifacts dropdown ──────────────────────────────────────────────────

    private void OnArtifactsClick(object? sender, EventArgs e)
    {
        // A plain Flyout with CheckBoxes (not a MenuFlyout) so it stays open while several artifacts
        // are toggled — a MenuFlyout dismisses on every item click, which fights multi-select.
        var panel = new StackPanel { MinWidth = 190 };
        panel.Children.Add(BuildMenuHeader(AppStrings.MonitorArtifacts, AppStrings.MonitorArtifactsInfo));

        // Each row registers a refresh action so the "None" button can re-sync all rows in place.
        var refreshers = new List<Action>();
        AddArtifactCheck(panel, refreshers, AppStrings.MonitorArtifactMuscle, EcgArtifacts.Muscle);
        AddArtifactCheck(panel, refreshers, AppStrings.MonitorArtifactMains, EcgArtifacts.Mains);
        AddArtifactCheck(panel, refreshers, AppStrings.MonitorArtifactBaseline, EcgArtifacts.Baseline);
        AddArtifactCheck(panel, refreshers, AppStrings.MonitorArtifactContact, EcgArtifacts.Contact);
        AddArtifactCheck(panel, refreshers, AppStrings.MonitorArtifactMotion, EcgArtifacts.Motion);

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = AppTheme.ControlBorder,
            Margin = new Thickness(0, 6, 0, 6),
        });

        // "None" clears every active artifact at once, without closing the flyout.
        var clear = new Button
        {
            Content = AppStrings.MonitorArtifactNone,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        clear.Click += (_, _) =>
        {
            ApplyArtifacts(EcgArtifacts.None);
            foreach (var r in refreshers) r();
        };
        panel.Children.Add(clear);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.Top, // open upward over the monitor (panel sits at the bottom)
        };
        flyout.ShowAt(ArtifactsTab);
    }

    // Custom checkbox row (box + label both vertically centred). The stock WinUI CheckBox pins its box
    // toward the top of a fixed 32px row, so a centred label never lines up with it; this also lets the
    // tick use the app accent instead of the system colour.
    private void AddArtifactCheck(StackPanel panel, List<Action> refreshers, string text, EcgArtifacts artifact)
    {
        var glyph = new FontIcon
        {
            Glyph = "", // checkmark
            FontSize = 12,
            Foreground = AppTheme.OnAccent,
        };
        var box = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = AppTheme.ControlBorder,
            BorderThickness = new Thickness(1),
            Child = glyph,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = text,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(box);
        row.Children.Add(label);

        var container = new Border
        {
            Child = row,
            Padding = new Thickness(6, 5, 10, 5),
            CornerRadius = new CornerRadius(4),
            Background = Transparent,
        };

        void Refresh()
        {
            var on = _artifacts.HasFlag(artifact);
            box.Background = on ? AppTheme.Accent : Transparent;
            box.BorderBrush = on ? AppTheme.Accent : AppTheme.ControlBorder;
            glyph.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        Refresh();
        refreshers.Add(Refresh);

        container.Tapped += (_, _) =>
        {
            var next = _artifacts.HasFlag(artifact) ? _artifacts & ~artifact : _artifacts | artifact;
            ApplyArtifacts(next);
            Refresh();
        };
        container.PointerEntered += (_, _) => container.Background = AppTheme.HoverFill;
        container.PointerExited += (_, _) => container.Background = Transparent;

        panel.Children.Add(container);
    }

    private void ApplyArtifacts(EcgArtifacts artifacts)
    {
        _artifacts = artifacts;
        ArtifactsTab.IsActive = artifacts != EcgArtifacts.None;
        ArtifactsTab.Text = ArtifactsLabel(artifacts);
        ArtifactSelected?.Invoke(this, artifacts);
    }

    // ── Filters dropdown ───────────────────────────────────────────────────

    private void OnFiltersClick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;

        // A plain Flyout (not a MenuFlyout) so it can host the signal-quality badge — moved here from
        // the monitor overlay — above the filter chooser. The badge reflects the quality of the
        // currently displayed (filtered) trace, so the two belong together.
        var panel = new StackPanel { MinWidth = 230 };
        panel.Children.Add(BuildMenuHeader(AppStrings.MonitorFilters, AppStrings.MonitorFiltersInfo));
        panel.Children.Add(BuildSqiBadge());
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = AppTheme.ControlBorder,
            Margin = new Thickness(0, 8, 0, 6),
        });

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.Top, // open upward over the monitor (panel sits at the bottom)
        };

        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameNone, EcgFilterType.None);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameLp, EcgFilterType.Lowpass);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameHp, EcgFilterType.Highpass);
        AddFilterRow(panel, flyout, AppStrings.MonitorFilterNameBp, EcgFilterType.Bandpass);

        flyout.ShowAt(FiltersTab);
    }

    // The signal-quality (SQI) readout: a coloured dot + "Quality: <label> (<lead>)" and the three
    // backing indices. Shows a muted placeholder when the monitor hasn't computed a value yet
    // (no/short signal, or compare mode).
    private UIElement BuildSqiBadge()
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.TextPrimary,
        };
        var details = new TextBlock
        {
            FontSize = 11,
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var info = _viewModel?.SignalQuality;
        if (info is null)
        {
            dot.Fill = AppTheme.TextSecondary;
            label.Text = AppStrings.MonitorSignalQuality + ": —";
            details.Text = AppStrings.MonitorSignalQualityUnavailable;
        }
        else
        {
            dot.Fill = QualityBrush(info.Quality);
            label.Text = $"{AppStrings.MonitorSignalQuality}: {LocalizeQuality(info.Quality)} ({info.PrimaryLead})";
            details.Text =
                $"sSQI (skew): {info.SSqi:F2} | kSQI (kurt): {info.KSqi:F2} | pSQI (flat): {info.PSqi * 100:F1}%";
        }

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(dot);
        titleRow.Children.Add(label);

        var column = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        column.Children.Add(titleRow);
        column.Children.Add(details);
        return column;
    }

    private static string LocalizeQuality(string quality) => quality switch
    {
        "Excellent" => AppStrings.MonitorQualityExcellent,
        "Acceptable" => AppStrings.MonitorQualityAcceptable,
        "Barely acceptable" => AppStrings.MonitorQualityBarelyAcceptable,
        "Barely acceptable/Acceptable" => AppStrings.MonitorQualityBarelyAcceptableOrAcceptable,
        "Unacceptable" => AppStrings.MonitorQualityUnacceptable,
        _ => quality
    };

    private static SolidColorBrush QualityBrush(string quality) => quality switch
    {
        "Excellent" => new SolidColorBrush(Colors.LimeGreen),
        "Barely acceptable" or "Barely acceptable/Acceptable" => new SolidColorBrush(Colors.Gold),
        _ => new SolidColorBrush(Colors.Crimson),
    };

    // A single-select filter row: a check glyph marks the active filter (mirrors the old
    // RadioMenuFlyoutItem). Selecting applies the filter and closes the flyout.
    private void AddFilterRow(StackPanel panel, Flyout flyout, string text, EcgFilterType filterType)
    {
        if (_viewModel is null) return;
        var selected = _viewModel.MonitorMode.FilterType == filterType;

        var glyph = new FontIcon
        {
            Glyph = "", // checkmark
            FontSize = 12,
            Foreground = AppTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 18,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
        };
        var label = new TextBlock
        {
            Text = text,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(glyph);
        row.Children.Add(label);

        var container = new Border
        {
            Child = row,
            Padding = new Thickness(6, 6, 12, 6),
            CornerRadius = new CornerRadius(4),
            Background = Transparent,
        };
        container.Tapped += (_, _) =>
        {
            _viewModel.SetFilterType(filterType);
            flyout.Hide();
        };
        container.PointerEntered += (_, _) => container.Background = AppTheme.HoverFill;
        container.PointerExited += (_, _) => container.Background = Transparent;

        panel.Children.Add(container);
    }

    // ── Display dropdowns ───────────────────────────────────────────────────

    private void OnCountClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var count in new[] { 1, 2, 3, 4, 6, 12 })
        {
            var captured = count;
            var item = new MenuFlyoutItem { Text = AppStrings.MonitorCountFormat(captured) };
            item.Click += (_, _) => _viewModel?.SetSeriesCount(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(CountTab);
    }

    private void OnSchemeClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        AddSchemeItem(flyout, AppStrings.MonitorColumnsOne, SeriesScheme.OneColumn);
        AddSchemeItem(flyout, AppStrings.MonitorColumnsTwo, SeriesScheme.TwoColumn);
        AddSchemeItem(flyout, AppStrings.MonitorColumnsGrid, SeriesScheme.Grid);
        flyout.ShowAt(SchemeTab);
    }

    private void AddSchemeItem(MenuFlyout flyout, string text, SeriesScheme scheme)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => _viewModel?.SetSeriesScheme(scheme);
        flyout.Items.Add(item);
    }

    private void OnSpeedClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var speed in new[] { 12.5f, 25f, 50f, 100f })
        {
            var captured = speed;
            var label = captured % 1 == 0
                ? AppStrings.MonitorSpeedFormat((int)captured)
                : $"{captured} {AppStrings.MonitorSpeedUnit}";
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => _viewModel?.SetSpeed(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(SpeedTab);
    }

    private void OnScaleClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var scale in new[] { 1.0f, 2.0f, 4.0f })
        {
            var captured = scale;
            var item = new MenuFlyoutItem { Text = $"{(int)(captured * 100)}%" };
            item.Click += (_, _) => _viewModel?.SetScale(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ScaleTab);
    }

    private void OnStartStopClick(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        var newState = !_viewModel.MonitorMode.IsRunning;
        _viewModel.SetIsRunning(newState);
        StartStopClick?.Invoke(this, newState);
    }
}
