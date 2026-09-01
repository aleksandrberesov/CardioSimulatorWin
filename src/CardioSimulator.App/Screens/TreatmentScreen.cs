using System;
using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Domain.Treatment;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// «Лечение» — the treatment / resuscitation simulation (customer 28-08-2026). The shared live 12-lead
/// monitor on the left; a column of treatment action cards (IV drugs / defib / pills / pacing / vagal /
/// O₂ / CPR), a direct rhythm-change control, and the event log on the right. Actions run through the pure
/// <see cref="TreatmentEngine"/> via <see cref="TreatmentViewModel"/>, and the resulting rhythm is shown on
/// the monitor after the accelerated-clock delay. Mirrors the <see cref="OSKEScreen"/> hosting pattern.
/// </summary>
public sealed class TreatmentScreen : UserControl
{
    // Per-card accent colours (from the mockup), used as card fills with white text (yellow uses dark ink).
    private static readonly Color Green = Color.FromArgb(0xFF, 0x2E, 0xA0, 0x4A);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xE0, 0x3B, 0x30);
    private static readonly Color Blue = Color.FromArgb(0xFF, 0x1E, 0x6F, 0xE0);
    private static readonly Color Orange = Color.FromArgb(0xFF, 0xE8, 0x8A, 0x00);
    private static readonly Color Yellow = Color.FromArgb(0xFF, 0xE8, 0xC0, 0x00);
    private static readonly Color Cyan = Color.FromArgb(0xFF, 0x2E, 0xA6, 0xC7);
    private static readonly Color Pink = Color.FromArgb(0xFF, 0xE0, 0x2D, 0x55);
    private static readonly Color AlertRed = Color.FromArgb(0xFF, 0xD3, 0x3A, 0x2F); // shared app alert red (EOS/overlay)
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush Ink = new(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E));

    private TreatmentViewModel? _vm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private readonly MonitorView _monitor = new();
    // Both the monitor and the panel are built into the root exactly ONCE and never re-parented — the monitor
    // because its Win2D swap chain tears down on Unloaded, the panel because re-parenting its persistent
    // header/log/banner field elements throws in XAML. Selections and reset restyle controls in place instead.
    private readonly ContentControl _panelHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    private readonly TextBlock _statusText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _pendingText = new() { FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly StackPanel _logHost = new() { Spacing = 4 };
    // Cardiac-arrest CPR prompt (shown in the status header only while the rhythm is a pulseless arrest).
    private readonly TextBlock _arrestText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = White, TextWrapping = TextWrapping.Wrap };
    private readonly Border _arrestBanner = new()
    {
        Background = new SolidColorBrush(AlertRed),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    // Picker / control state.
    private TreatmentDrug? _selectedDrug;
    private TreatmentDrug? _selectedPill;
    private VagalManeuver? _selectedVagal;
    private double _doseMg;
    private int _energy = 200;
    private bool _sync;
    private int _paceRate = 70;
    private int _paceCurrent = 50;
    private ClinicalRhythmState _rhythmPick = ClinicalRhythmState.Sinus;
    private bool _shownOnce;

    // Live control references so selections restyle in place (no full-panel rebuild → no scroll jump / flicker).
    private NumberBox? _doseBox;
    private ToggleSwitch? _oxyToggle;
    private ToggleSwitch? _cprToggle;
    private ComboBox? _rhythmCombo;
    private bool _syncingToggles;
    // Instrument controls reset by «Отмена» (reset-all), and the «Применить» button (commit pending effect).
    private Slider? _energySlider;
    private Slider? _paceRateSlider;
    private Slider? _paceCurrentSlider;
    private ToggleSwitch? _syncToggle;
    private Button? _applyButton;
    // Registered pick buttons: (button, card colour, is-this-one-selected). Restyled together on any pick.
    private readonly System.Collections.Generic.List<(Button Btn, Color Bg, Func<bool> Active)> _picks = new();

    public TreatmentScreen()
    {
        _arrestBanner.Child = _arrestText;
        Content = new TextBlock { Text = string.Empty }; // replaced in Initialize once VMs are bound
    }

    public void Initialize(TreatmentViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;

        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;

        _vm.ShowRhythm = ShowRhythm;
        _vm.StateChanged += OnStateChanged;
        _vm.LogChanged += OnLogChanged;
        _rhythmVm.PropertyChanged += OnRhythmVmChanged;

        Content = BuildRoot();

        _monitorVm.SetSeriesCount(12);
        _monitorVm.SetSeriesScheme(SeriesScheme.TwoColumn);
        _monitorVm.SetIsRunning(true);
        _vm.ShowCurrent();
        RefreshStatus();
        RefreshLog();

        Unloaded += (_, _) =>
        {
            // Stop any pending delayed effect and drop the ShowRhythm hook BEFORE unsubscribing, so a queued
            // timer Tick can't fire after teardown and mutate the (now orphaned) shared rhythm view-model.
            _vm.Stop();
            _vm.ShowRhythm = null;
            _vm.StateChanged -= OnStateChanged;
            _vm.LogChanged -= OnLogChanged;
            _rhythmVm.PropertyChanged -= OnRhythmVmChanged;
            _monitorVm?.SetIsRunning(false);
        };
    }

    // The manifest loads after the screen is built; show the initial rhythm once the index is populated.
    private void OnRhythmVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms) && !_shownOnce && _rhythmVm?.Rhythms.Count > 0)
        {
            _shownOnce = true;
            _vm?.ShowCurrent();
        }
    }

    // ── State → rhythm resolution ─────────────────────────────────────────────

    private void ShowRhythm(ClinicalRhythmState state)
    {
        if (_rhythmVm is null || _appVm is null) return;
        if (TreatmentRhythmMap.IsSynthesizedFlatline(state)) { _rhythmVm.ShowFlatline(); return; }

        var all = _appVm.Repository.Pathologies();
        foreach (var acronym in TreatmentRhythmMap.AcronymsFor(state))
        {
            var ids = Taxonomy.ResolvePathologyIdsForAcronyms(new[] { acronym }, all);
            if (ids.Count > 0) { _rhythmVm.SelectRhythm(ids[0], persist: false); return; }
        }
        // No representative rhythm in the pak for this state (only reachable on a reduced/custom pak). The
        // monitor keeps the previous trace, which would silently contradict the status/log — surface it so the
        // divergence is visible rather than misleading. Skip during initial load (index not yet populated).
        if (_rhythmVm.Rhythms.Count > 0)
            _vm?.LogSystem(AppStrings.TreatmentLogUnresolvedFormat(AppStrings.TreatmentStateName(state)));
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private UIElement BuildRoot()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        Grid.SetColumn(_monitor, 0);
        grid.Children.Add(_monitor);

        _panelHost.Content = BuildPanel();
        Grid.SetColumn(_panelHost, 1);
        grid.Children.Add(_panelHost);
        return grid;
    }

    private UIElement BuildPanel()
    {
        _picks.Clear(); // buttons from a prior build are discarded; don't keep restyling them
        var root = new Grid { Padding = new Thickness(10, 8, 10, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // actions (scroll)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) }); // log

        // Header: «Лечение» title + Отмена (reset-all, confirmed) + Применить (commit pending effect now).
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = AppStrings.ModeName(OperatingMode.Treatment),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        titleRow.Children.Add(title);
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var cancel = new Button { Content = AppStrings.CommonCancel, Padding = new Thickness(12, 4, 12, 4) };
        cancel.Click += (_, _) => _ = ResetAllAsync();
        _applyButton = new Button
        {
            Content = AppStrings.CommonApply,
            Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Green),
            Foreground = White,
        };
        _applyButton.Click += (_, _) => ApplyPending();
        headerButtons.Children.Add(cancel);
        headerButtons.Children.Add(_applyButton);
        Grid.SetColumn(headerButtons, 1);
        titleRow.Children.Add(headerButtons);
        header.Children.Add(titleRow);

        _statusText.Foreground = AppTheme.TextPrimary;
        header.Children.Add(_statusText);
        _pendingText.Foreground = AppTheme.Accent;
        header.Children.Add(_pendingText);
        header.Children.Add(_arrestBanner);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Action cards (scrollable).
        var cards = new StackPanel { Spacing = 8 };
        cards.Children.Add(BuildIvDrugCard());
        cards.Children.Add(BuildDefibCard());
        cards.Children.Add(BuildPillCard());
        cards.Children.Add(BuildPacingCard());
        cards.Children.Add(BuildVagalCard());
        var toggles = new Grid { ColumnSpacing = 8 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var oxy = (FrameworkElement)BuildOxygenCard(); Grid.SetColumn(oxy, 0); toggles.Children.Add(oxy);
        var cpr = (FrameworkElement)BuildCprCard(); Grid.SetColumn(cpr, 1); toggles.Children.Add(cpr);
        cards.Children.Add(toggles);
        cards.Children.Add(BuildRhythmChangeCard());
        cards.Children.Add(BuildSpeedControl());
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Event log.
        var logCard = new Border
        {
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
        };
        var logStack = new StackPanel { Spacing = 6 };
        logStack.Children.Add(new TextBlock { Text = AppStrings.TxEventLog, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary });
        logStack.Children.Add(new ScrollViewer { Content = _logHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        logCard.Child = logStack;
        Grid.SetRow(logCard, 2);
        root.Children.Add(logCard);

        return root;
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    private UIElement Card(Color bg, string icon, string title, UIElement body)
    {
        var textBrush = bg == Yellow ? Ink : White;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(head);
        stack.Children.Add(body);
        return new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = stack,
        };
    }

    // A selectable pick chip. Registers itself in _picks so choosing one restyles the whole group in place —
    // no RebuildPanel (which would reset the scroll position and flicker the panel).
    private Button PickButton(string text, Color cardBg, Func<bool> isActive, Action onClick)
    {
        var textBrush = cardBg == Yellow ? Ink : White;
        var btn = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 11, Foreground = textBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 5, 6, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        StylePick(btn, isActive());
        btn.Click += (_, _) => { onClick(); RestylePicks(); };
        _picks.Add((btn, cardBg, isActive));
        return btn;
    }

    private static void StylePick(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(active ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        btn.BorderBrush = new SolidColorBrush(active ? Colors.White : Colors.Transparent);
    }

    private void RestylePicks()
    {
        foreach (var (btn, _, active) in _picks) StylePick(btn, active());
    }

    private UIElement BuildIvDrugCard()
    {
        var body = new StackPanel { Spacing = 6 };
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var drugs = new[] { TreatmentDrug.Adrenaline, TreatmentDrug.Amiodarone, TreatmentDrug.Atropine, TreatmentDrug.MagnesiumSulfate, TreatmentDrug.CalciumChloride, TreatmentDrug.Adenosine };
        for (var i = 0; i < drugs.Length; i++)
        {
            var drug = drugs[i];
            var b = PickButton(AppStrings.TreatmentDrugName(drug), Green, () => _selectedDrug == drug, () =>
            {
                _selectedDrug = drug;
                _doseMg = DrugCatalog.StandardDoseMg(drug);
                if (_doseBox is not null) _doseBox.Value = _doseMg; // reflect the standard dose without a rebuild
            });
            Grid.SetRow(b, i / 2); Grid.SetColumn(b, i % 2);
            if (i / 2 >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(b);
        }
        body.Children.Add(grid);

        var doseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _doseBox = new NumberBox { Value = _selectedDrug is { } sd ? DrugCatalog.StandardDoseMg(sd) : double.NaN, PlaceholderText = "0", Minimum = 0, SmallChange = 0.5, Width = 90, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _doseBox.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) _doseMg = e.NewValue; };
        doseRow.Children.Add(_doseBox);
        doseRow.Children.Add(new TextBlock { Text = AppStrings.TxUnitMg, Foreground = White, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var give = CardButton(AppStrings.TxBtnGive, Green);
        give.Click += (_, _) =>
        {
            if (_selectedDrug is not { } d) { Toast(AppStrings.TxPickDrug); return; }
            // A blank/zero dose falls back to the drug's standard dose so the administered (and logged) amount
            // always matches a real value; Minimum=0 on the box already blocks negatives.
            var dose = double.IsNaN(_doseMg) || _doseMg <= 0 ? DrugCatalog.StandardDoseMg(d) : _doseMg;
            TryApply(new TreatmentAction.Drug(d, dose));
        };
        doseRow.Children.Add(give);
        body.Children.Add(doseRow);

        return Card(Green, "💉", AppStrings.TxCardIv, body);
    }

    private UIElement BuildDefibCard()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(SliderRow(AppStrings.TxEnergy, 50, 360, 50, _energy, v => _energy = v, v => $"{v} {AppStrings.TxUnitJoules}", s => _energySlider = s));
        var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        syncRow.Children.Add(new TextBlock { Text = AppStrings.TxSync, Foreground = White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _syncToggle = new ToggleSwitch { IsOn = _sync, OnContent = null, OffContent = null, MinWidth = 0 };
        _syncToggle.Toggled += (_, _) => _sync = _syncToggle.IsOn;
        syncRow.Children.Add(_syncToggle);
        body.Children.Add(syncRow);
        var shock = new Button
        {
            Content = new TextBlock { Text = AppStrings.TxBtnShock, Foreground = new SolidColorBrush(Red), FontWeight = FontWeights.Bold },
            Background = White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        shock.Click += (_, _) => TryApply(new TreatmentAction.Defib(_energy, _sync));
        body.Children.Add(shock);
        return Card(Red, "⚡", AppStrings.TxCardDefib, body);
    }

    private UIElement BuildPillCard()
    {
        var body = new StackPanel { Spacing = 6 };
        var grid = new Grid { ColumnSpacing = 4 };
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pills = new[] { TreatmentDrug.Nitroglycerin, TreatmentDrug.Aspirin, TreatmentDrug.Metoprolol };
        for (var i = 0; i < pills.Length; i++)
        {
            var pill = pills[i];
            var b = PickButton(AppStrings.TreatmentDrugName(pill), Blue, () => _selectedPill == pill, () => _selectedPill = pill);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
        body.Children.Add(grid);
        var give = CardButton(AppStrings.TxBtnGive, Blue);
        give.HorizontalAlignment = HorizontalAlignment.Left;
        give.Click += (_, _) => { if (_selectedPill is { } p) TryApply(new TreatmentAction.Drug(p, DrugCatalog.StandardDoseMg(p))); else Toast(AppStrings.TxPickDrug); };
        body.Children.Add(give);
        return Card(Blue, "💊", AppStrings.TxCardPill, body);
    }

    private UIElement BuildPacingCard()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(SliderRow(AppStrings.TxRate, 40, 120, 5, _paceRate, v => _paceRate = v, v => $"{v} {AppStrings.TxUnitBpm}", s => _paceRateSlider = s));
        body.Children.Add(SliderRow(AppStrings.TxCurrent, 0, 200, 5, _paceCurrent, v => _paceCurrent = v, v => $"{v} {AppStrings.TxUnitMa}", s => _paceCurrentSlider = s));
        var start = CardButton(AppStrings.TxBtnStartPacing, Orange);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        start.Click += (_, _) => TryApply(new TreatmentAction.Pacing(_paceRate, _paceCurrent));
        body.Children.Add(start);
        return Card(Orange, "🫀", AppStrings.TxCardPacing, body);
    }

    private UIElement BuildVagalCard()
    {
        var body = new StackPanel { Spacing = 6 };
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var maneuvers = new[] { VagalManeuver.Valsalva, VagalManeuver.CarotidSinusMassage };
        for (var i = 0; i < maneuvers.Length; i++)
        {
            var m = maneuvers[i];
            var b = PickButton(AppStrings.TreatmentVagalName(m), Yellow, () => _selectedVagal == m, () => _selectedVagal = m);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
        body.Children.Add(grid);
        var perform = CardButton(AppStrings.TxBtnPerform, Yellow);
        perform.HorizontalAlignment = HorizontalAlignment.Left;
        perform.Click += (_, _) => { if (_selectedVagal is { } v) TryApply(new TreatmentAction.Vagal(v)); else Toast(AppStrings.TxPickManeuver); };
        body.Children.Add(perform);
        return Card(Yellow, "〰️", AppStrings.TxCardVagal, body);
    }

    private UIElement BuildOxygenCard()
    {
        _oxyToggle = new ToggleSwitch { IsOn = _vm?.Context.OxygenOn ?? false, OnContent = AppStrings.TxOn, OffContent = AppStrings.TxOff };
        _oxyToggle.Toggled += (_, _) => { if (!_syncingToggles) TryApply(new TreatmentAction.Oxygen(_oxyToggle.IsOn)); };
        return Card(Cyan, "🌬️", AppStrings.TxCardOxygen, _oxyToggle);
    }

    private UIElement BuildCprCard()
    {
        _cprToggle = new ToggleSwitch { IsOn = _vm?.Context.CprActive ?? false, OnContent = AppStrings.TxOn, OffContent = AppStrings.TxOff };
        _cprToggle.Toggled += (_, _) => { if (!_syncingToggles) TryApply(new TreatmentAction.Cpr(_cprToggle.IsOn)); };
        return Card(Pink, "🫁", AppStrings.TxCardCpr, _cprToggle);
    }

    // Push the authoritative context state back onto the toggles (after an apply, a reset, or a declined
    // confirm) without re-triggering their Toggled → TryApply handlers.
    private void SyncToggles()
    {
        if (_vm is null) return;
        _syncingToggles = true;
        if (_oxyToggle is not null) _oxyToggle.IsOn = _vm.Context.OxygenOn;
        if (_cprToggle is not null) _cprToggle.IsOn = _vm.Context.CprActive;
        _syncingToggles = false;
    }

    private UIElement BuildRhythmChangeCard()
    {
        var body = new StackPanel { Spacing = 6 };
        _rhythmCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in Enum.GetValues<ClinicalRhythmState>())
            _rhythmCombo.Items.Add(new ComboBoxItem { Content = AppStrings.TreatmentStateName(s), Tag = s });
        _rhythmPick = _vm?.CurrentState ?? ClinicalRhythmState.Sinus;
        _rhythmCombo.SelectedIndex = (int)_rhythmPick; // ClinicalRhythmState is a plain enum → value == index
        _rhythmCombo.SelectionChanged += (_, _) => { if ((_rhythmCombo.SelectedItem as ComboBoxItem)?.Tag is ClinicalRhythmState s) _rhythmPick = s; };
        body.Children.Add(_rhythmCombo);
        var set = new Button { Content = AppStrings.TxBtnSetRhythm, HorizontalAlignment = HorizontalAlignment.Stretch };
        set.Click += (_, _) => TryApply(new TreatmentAction.SetRhythm(_rhythmPick));
        body.Children.Add(set);
        return new Border
        {
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = WithHeader(AppStrings.TxChangeRhythm, body),
        };
    }

    private UIElement BuildSpeedControl()
    {
        var body = SliderRowThemed(AppStrings.TxSpeed, 10, 240, 10, (int)(_vm?.SpeedFactor ?? 60),
            v => { if (_vm is not null) _vm.SpeedFactor = v; }, v => $"×{v}");
        return new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            Child = body,
        };
    }

    private static UIElement WithHeader(string title, UIElement body)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary });
        s.Children.Add(body);
        return s;
    }

    // A slider row on a coloured card (white label/value). `capture` receives the Slider so «Отмена» can reset
    // it in place (setting Value re-fires ValueChanged, updating the field and the value label).
    private UIElement SliderRow(string label, int min, int max, int step, int value, Action<int> onChange, Func<int, string> fmt, Action<Slider>? capture = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, Foreground = White, FontSize = 11, MinWidth = 56, VerticalAlignment = VerticalAlignment.Center });
        var valueText = new TextBlock { Text = fmt(value), Foreground = White, FontSize = 11, FontWeight = FontWeights.SemiBold, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { var v = (int)e.NewValue; onChange(v); valueText.Text = fmt(v); };
        capture?.Invoke(slider);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        return row;
    }

    private UIElement SliderRowThemed(string label, int min, int max, int step, int value, Action<int> onChange, Func<int, string> fmt)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, Foreground = AppTheme.TextSecondary, FontSize = 12, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center });
        var valueText = new TextBlock { Text = fmt(value), Foreground = AppTheme.TextPrimary, FontSize = 12, FontWeight = FontWeights.SemiBold, MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = 140, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { var v = (int)e.NewValue; onChange(v); valueText.Text = fmt(v); };
        row.Children.Add(slider);
        row.Children.Add(valueText);
        return row;
    }

    private Button CardButton(string text, Color cardBg)
    {
        var textBrush = cardBg == Yellow ? Ink : White;
        return new Button
        {
            Content = new TextBlock { Text = text, Foreground = textBrush, FontSize = 11, FontWeight = FontWeights.SemiBold },
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
        };
    }

    // ── Header actions (Отмена / Применить) ────────────────────────────────────

    // «Отмена»: reset the whole scenario after a confirmation — selections, dose, rhythm, the instrument
    // settings (defib energy, pacer rate/output, sync), the engine/context and the event log. Everything is
    // reset IN PLACE (no panel rebuild — the persistent header/log/banner fields must not be re-parented).
    private async System.Threading.Tasks.Task ResetAllAsync()
    {
        if (_vm is null || !await ConfirmAsync(AppStrings.TxConfirmResetAll)) return;
        _selectedDrug = null; _selectedPill = null; _selectedVagal = null; _doseMg = 0;
        if (_doseBox is not null) _doseBox.Value = double.NaN;
        if (_rhythmCombo is not null) _rhythmCombo.SelectedIndex = 0;
        _rhythmPick = ClinicalRhythmState.Sinus;
        if (_energySlider is not null) _energySlider.Value = 200;       // ValueChanged updates the field + label
        if (_paceRateSlider is not null) _paceRateSlider.Value = 70;
        if (_paceCurrentSlider is not null) _paceCurrentSlider.Value = 50;
        if (_syncToggle is not null) _syncToggle.IsOn = false;
        _vm.Reset();     // clears engine/context/log; fires StateChanged → status/banner/toggles refresh
        RestylePicks();  // drop the chip highlights
    }

    // «Применить»: commit any in-progress delayed effect now (skip the accelerated-clock wait). The button is
    // enabled only while an effect is pending, so the toast is just a defensive fallback.
    private void ApplyPending()
    {
        if (_vm is null) return;
        if (!_vm.CommitPendingNow()) Toast(AppStrings.TxNoPending);
    }

    // ── Apply / validate ──────────────────────────────────────────────────────

    private async void TryApply(TreatmentAction action)
    {
        if (_vm is null) return;
        var v = _vm.Validate(action);
        var message = AppStrings.TreatmentReasonText(v.Reason, action); // localized (drug/limit inlined)
        if (v.Verdict == TreatmentVerdict.Block)
        {
            await InfoAsync(string.IsNullOrEmpty(message) ? AppStrings.TreatmentLogNoEffect : message);
            _vm.Apply(action); // logs the blocked reason; no rhythm change
            return;
        }
        if (v.Verdict == TreatmentVerdict.Warn && !await ConfirmAsync(message))
        {
            SyncToggles(); // a declined O₂/CPR toggle must snap back to the real context state
            return;
        }
        _vm.Apply(action);
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = AppStrings.ModeName(OperatingMode.Treatment),
            Content = message,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        try { return await dlg.ShowAsync() == ContentDialogResult.Primary; }
        catch { return false; }
    }

    private async System.Threading.Tasks.Task InfoAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = AppStrings.ModeName(OperatingMode.Treatment),
            Content = message,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        try { await dlg.ShowAsync(); } catch { /* ignore */ }
    }

    private void Toast(string message) => _ = InfoAsync(message);

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void OnStateChanged() { RefreshStatus(); SyncToggles(); }

    private void OnLogChanged() { RefreshLog(); }

    private void RefreshStatus()
    {
        if (_vm is null) return;
        _statusText.Text = AppStrings.TxStatusFormat(AppStrings.TreatmentStateName(_vm.CurrentState));
        _pendingText.Text = _vm.PendingState is { } ps
            ? AppStrings.TxPendingTargetFormat(AppStrings.TreatmentStateName(ps))
            : AppStrings.TxPending;
        _pendingText.Visibility = _vm.HasPendingEffect ? Visibility.Visible : Visibility.Collapsed;
        // «Применить» fast-forwards a pending effect — enabled only while one is in progress.
        if (_applyButton is not null)
        {
            _applyButton.IsEnabled = _vm.HasPendingEffect;
            _applyButton.Opacity = _vm.HasPendingEffect ? 1.0 : 0.5;
        }

        // Cardiac-arrest CPR prompt: visible only in a pulseless-arrest rhythm; the message nudges toward CPR
        // when it isn't running, and acknowledges it when it is.
        if (TreatmentRhythmMap.IsArrestRhythm(_vm.CurrentState))
        {
            _arrestText.Text = _vm.Context.CprActive ? AppStrings.TxArrestCprOngoing : AppStrings.TxArrestStartCpr;
            _arrestBanner.Opacity = _vm.Context.CprActive ? 0.75 : 1.0; // calmer once compressions are underway
            _arrestBanner.Visibility = Visibility.Visible;
        }
        else
        {
            _arrestBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshLog()
    {
        if (_vm is null) return;
        _logHost.Children.Clear();
        foreach (var entry in _vm.Log.Take(60))
        {
            var color = entry.Kind switch
            {
                TreatmentLogKind.Warning => AppTheme.Negative,
                TreatmentLogKind.Outcome => AppTheme.Positive,
                TreatmentLogKind.Action => AppTheme.TextPrimary,
                _ => AppTheme.TextSecondary,
            };
            var line = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = color };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.Time + "  ", Foreground = AppTheme.TextSecondary });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.Message });
            _logHost.Children.Add(line);
        }
    }

}
