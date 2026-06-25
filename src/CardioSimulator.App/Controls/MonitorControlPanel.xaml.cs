using System.ComponentModel;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The kinds of recording artifact that can be overlaid on the monitor trace, offered by the
/// "Артефакты" dropdown. <see cref="None"/> clears any active artifact.
/// </summary>
public enum EcgArtifact
{
    None,
    Muscle,
    Mains,
    Baseline,
    Contact,
    Motion,
}

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

    // pQRSt active state: blue fill + white text, matching the spec's toggled-on chip.
    private static readonly SolidColorBrush PqrstActiveFill =
        new(new Windows.UI.Color { A = 255, R = 0x1E, G = 0x88, B = 0xE5 });
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush Black = new(Colors.Black);
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    // Light gray press/hover wash, matching the Tab control's affordance.
    private static readonly SolidColorBrush HoverFill =
        new(new Windows.UI.Color { A = 30, R = 128, G = 128, B = 128 });

    private MonitorViewModel? _viewModel;
    private bool _pqrstActive;
    private EcgArtifact _artifact = EcgArtifact.None;

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

    /// <summary>Raised when an artifact is chosen from the Artifacts dropdown.</summary>
    public event EventHandler<EcgArtifact>? ArtifactSelected;

    public MonitorControlPanel()
    {
        InitializeComponent();
        SetStaticLabels();
    }

    private void SetStaticLabels()
    {
        ElectrodesTab.Text = AppStrings.MonitorElectrodes;
        ArtifactsTab.Text = AppStrings.MonitorArtifacts;
        EosText.Text = AppStrings.MonitorEos;
        TipsTab.Text = AppStrings.MonitorTips;
        SpeedTab.SubText = AppStrings.MonitorSpeedUnit;
        CompareTab.Text = AppStrings.CompareButton;
    }

    private void OnCompareClick(object? sender, EventArgs e) => CompareClick?.Invoke(this, EventArgs.Empty);
    private void OnElectrodesClick(object? sender, EventArgs e) => ElectrodesClick?.Invoke(this, EventArgs.Empty);
    private void OnTipsClick(object? sender, EventArgs e) => TipsClick?.Invoke(this, EventArgs.Empty);

    private void OnHeart3DTapped(object sender, TappedRoutedEventArgs e) => Heart3DClick?.Invoke(this, EventArgs.Empty);
    private void OnHeart3DPointerEntered(object sender, PointerRoutedEventArgs e) => Heart3DButton.Background = HoverFill;
    private void OnHeart3DPointerExited(object sender, PointerRoutedEventArgs e) => Heart3DButton.Background = Transparent;

    private void OnEosTapped(object sender, TappedRoutedEventArgs e) => EosClick?.Invoke(this, EventArgs.Empty);
    private void OnEosPointerEntered(object sender, PointerRoutedEventArgs e) => EosButton.Background = HoverFill;
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
        StartStopTab.Glyph = mode.IsRunning ? GlyphStop : GlyphPlay;
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
        PqrstButton.Background = _pqrstActive ? PqrstActiveFill : Transparent;
        PqrstText.Foreground = _pqrstActive ? White : Black;
    }

    // ── Artifacts dropdown ──────────────────────────────────────────────────

    private void OnArtifactsClick(object? sender, EventArgs e)
    {
        var flyout = new MenuFlyout();
        AddArtifactItem(flyout, AppStrings.MonitorArtifactNone, EcgArtifact.None);
        AddArtifactItem(flyout, AppStrings.MonitorArtifactMuscle, EcgArtifact.Muscle);
        AddArtifactItem(flyout, AppStrings.MonitorArtifactMains, EcgArtifact.Mains);
        AddArtifactItem(flyout, AppStrings.MonitorArtifactBaseline, EcgArtifact.Baseline);
        AddArtifactItem(flyout, AppStrings.MonitorArtifactContact, EcgArtifact.Contact);
        AddArtifactItem(flyout, AppStrings.MonitorArtifactMotion, EcgArtifact.Motion);
        flyout.ShowAt(ArtifactsTab);
    }

    private void AddArtifactItem(MenuFlyout flyout, string text, EcgArtifact artifact)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "monitor_artifacts",
            IsChecked = _artifact == artifact,
        };
        item.Click += (_, _) =>
        {
            _artifact = artifact;
            ArtifactSelected?.Invoke(this, artifact);
        };
        flyout.Items.Add(item);
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
        foreach (var scale in new[] { 0.25f, 0.5f, 1.0f, 2.0f, 4.0f })
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
