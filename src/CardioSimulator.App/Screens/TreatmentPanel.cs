using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
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
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using CardioSimulator.App.Data;
using Microsoft.UI.Dispatching;

namespace CardioSimulator.App.Screens;

/// <summary>
/// «Лечение» — the treatment / resuscitation panel, a PART OF TEACHING (not a mode). It is toggled from a
/// bottom-bar button and docked as an overlay over the shared Teaching monitor (see
/// <see cref="Controls.TreatmentPanelWindow"/>). It has no monitor of its own — it drives the SHARED
/// <see cref="RhythmViewModel"/> that Teaching already runs. It seeds its state from the currently-displayed
/// real rhythm (classified via taxonomy acronyms — no abstract picker); action cards run through the pure
/// <see cref="TreatmentEngine"/> via <see cref="TreatmentViewModel"/>, and the resulting rhythm is shown on
/// the shared monitor after the accelerated-clock delay.
/// </summary>
public sealed class TreatmentPanel : UserControl
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
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private bool IsRussian => _appVm?.SelectedLanguage == CardioSimulator.Core.Domain.Language.RU;
    private Action? _onClose;
    // True while the panel is itself changing the displayed rhythm (an intervention committing), so the
    // resulting RhythmViewModel change does not re-seed the engine from the monitor and cause a feedback loop.
    private bool _selfDrivingRhythm;
    // The panel is built into Content exactly ONCE and never re-parented — re-parenting its persistent
    // header/log/banner field elements throws in XAML. Selections and reset restyle controls in place instead.

    // Theme-following brushes owned by the panel. Every themed surface/text references one of these (never a
    // baked AppTheme brush), so a live theme switch recolours the whole panel in place via ApplyTheme — the
    // panel can't simply be rebuilt (see above).
    private readonly SolidColorBrush _textPrimary = new();
    private readonly SolidColorBrush _textSecondary = new();
    private readonly SolidColorBrush _cardBackground = new();
    private readonly SolidColorBrush _cardBorder = new();
    private readonly SolidColorBrush _subtleFill = new();

    private readonly TextBlock _statusText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _pendingText = new() { FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly StackPanel _logHost = new() { Spacing = 4 };

    // Authored «Протоколы лечения» content: drives the engine outcomes (via the view-model's AuthoredTable)
    // AND is shown here as the applicable protocol steps for the current rhythm. Loaded once at Initialize.
    private Data.TreatmentProtocolSet? _protocolSet;
    private readonly StackPanel _protocolHost = new() { Spacing = 4 };
    private Border? _protocolCard; // collapses when no authored transition applies to the current rhythm
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

    private readonly ProgressBar _pendingProgressBar = new()
    {
        Height = 4,
        Margin = new Thickness(0, 2, 0, 2),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _pendingCountdownText = new()
    {
        FontSize = 11,
        Foreground = AppTheme.Accent,
        Visibility = Visibility.Collapsed,
    };

    // CPR animation state
    private DispatcherQueueTimer? _cprAnimTimer;
    private int _cprCompressionCount = 0;
    private int _cprCycleCount = 1;
    private readonly TextBlock _cprAnimText = new()
    {
        FontSize = 11,
        Foreground = AppTheme.Accent,
        FontWeight = FontWeights.SemiBold,
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly ProgressBar _cprProgressBar = new()
    {
        Minimum = 0,
        Maximum = 30,
        Height = 4,
        Margin = new Thickness(0, 2, 0, 2),
        Visibility = Visibility.Collapsed,
    };

    // Picker / control state.
    private TreatmentDrug? _selectedDrug;
    private TreatmentDrug? _selectedPill;
    private CustomDrugItem? _selectedCustomDrug;
    private VagalManeuver? _selectedVagal;
    private double _doseMg;
    private int _energy = 200;
    private bool _sync;
    private int _paceRate = 70;
    private int _paceCurrent = 50;

    // Live control references so selections restyle in place (no full-panel rebuild → no scroll jump / flicker).
    private NumberBox? _doseBox;
    private ToggleSwitch? _oxyToggle;
    private ToggleSwitch? _cprToggle;
    private bool _syncingToggles;
    // Instrument controls reset by «Отмена» (reset-all), and the «Применить» button (commit pending effect).
    private Slider? _energySlider;
    private Slider? _paceRateSlider;
    private Slider? _paceCurrentSlider;
    private ToggleSwitch? _syncToggle;
    private Button? _applyButton;
    // Registered pick buttons: (button, card colour, is-this-one-selected). Restyled together on any pick.
    private readonly System.Collections.Generic.List<(Button Btn, Color Bg, Func<bool> Active)> _picks = new();

    public TreatmentPanel()
    {
        _arrestBanner.Child = _arrestText;
        Content = new TextBlock { Text = string.Empty }; // replaced in Initialize once VMs are bound
    }

    /// <summary>Binds the panel to the SHARED Teaching rhythm view-model and seeds from the current rhythm.
    /// <paramref name="onClose"/> (optional) is invoked by the panel's ✕ button to close the overlay.</summary>
    public void Initialize(TreatmentViewModel vm, RhythmViewModel rhythmVm, AppViewModel appVm, Action? onClose = null)
    {
        _vm = vm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _onClose = onClose;

        _vm.ShowRhythm = ShowRhythm;
        _vm.StateChanged += OnStateChanged;
        _vm.LogChanged += OnLogChanged;
        _rhythmVm.PropertyChanged += OnRhythmVmChanged;

        // Load the instructor-authored protocols: they DRIVE the engine (via the view-model's authored table)
        // and are shown below as the applicable steps for the current rhythm.
        _protocolSet = appVm.TreatmentProtocolStore.Load();
        _vm.AuthoredTable = Data.TreatmentProtocolBridge.BuildTable(_protocolSet);

        ApplyTheme(); // seed the owned brushes before BuildPanel hands them out
        Content = BuildPanel();
        // Clicking empty space drops focus from the dose field so its spin buttons collapse.
        FieldFocus.DismissFieldFocusOnEmptyClick(this);

        SeedFromCurrentRhythm(); // seed the engine state from whatever the Teaching monitor already shows
        RefreshStatus();
        RefreshLog();

        Unloaded += (_, _) => Teardown();
    }

    /// <summary>Stops the pending-effect timer and unsubscribes, so a queued timer Tick can't fire after the
    /// overlay closes and mutate the shared rhythm view-model. Called from Unloaded and by the overlay host on
    /// close. Idempotent.</summary>
    public void Teardown()
    {
        if (_vm is null) return;
        StopCprAnimation();
        _vm.Stop();
        _vm.ShowRhythm = null;
        _vm.StateChanged -= OnStateChanged;
        _vm.LogChanged -= OnLogChanged;
        if (_rhythmVm is not null) _rhythmVm.PropertyChanged -= OnRhythmVmChanged;
    }

    /// <summary>Recolours the panel's themed surfaces and text for the active <see cref="AppTheme"/> in place
    /// (the owned brushes are shared by every themed element, including log/protocol lines). Default-styled
    /// controls follow the host's RequestedTheme, which the overlay host updates alongside this call.</summary>
    public void ApplyTheme()
    {
        _textPrimary.Color = AppTheme.TextPrimaryColor;
        _textSecondary.Color = AppTheme.TextSecondaryColor;
        _cardBackground.Color = AppTheme.AppCardBackgroundColor;
        _cardBorder.Color = AppTheme.AppCardBorderColor;
        _subtleFill.Color = AppTheme.AppSubtleFillColor;
    }

    // When the user selects a DIFFERENT Teaching rhythm (not a treatment-driven change), re-seed the engine so
    // an intervention transitions from the real displayed rhythm.
    private void OnRhythmVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selfDrivingRhythm) return;
        if (e.PropertyName is nameof(RhythmViewModel.SelectedRhythm) or nameof(RhythmViewModel.Rhythms))
            SeedFromCurrentRhythm();
    }

    // Classify the currently-displayed real rhythm (by its taxonomy acronyms) and seed the engine state. A
    // rhythm with no ACLS category (most diagnostic ECGs) leaves the state as-is.
    private void SeedFromCurrentRhythm()
    {
        if (_rhythmVm?.SelectedRhythm is not { } entry) return;
        if (TreatmentRhythmMap.ClassifyByAcronyms(entry.AcronymList) is { } state)
        {
            var title = IsRussian ? entry.ResolvedNameRu ?? entry.TitleEn : entry.TitleEn;
            _vm?.SeedState(state, entry.Id, title);
        }
    }

    // ── State → rhythm resolution ─────────────────────────────────────────────

    private void ShowRhythm(ClinicalRhythmState state, string? targetPathologyId = null)
    {
        if (_rhythmVm is null || _appVm is null) return;
        _selfDrivingRhythm = true; // this rhythm change is treatment-driven — don't let it re-seed the engine
        try
        {
            if (!string.IsNullOrWhiteSpace(targetPathologyId))
            {
                var all = _appVm.Repository.Pathologies();
                if (all.Any(p => string.Equals(p.Id, targetPathologyId, StringComparison.OrdinalIgnoreCase)))
                {
                    _rhythmVm.SelectRhythm(targetPathologyId, persist: false, immediate: true);
                    return;
                }
            }

            if (TreatmentRhythmMap.IsSynthesizedFlatline(state)) { _rhythmVm.ShowFlatline(); return; }

            var allPathologies = _appVm.Repository.Pathologies();
            foreach (var acronym in TreatmentRhythmMap.AcronymsFor(state))
            {
                // Prefer the category's canonical rhythm (primary diagnosis, purest) over an arbitrary first
                // match — so a successful conversion shows clean sinus, not an SR-tagged AV-block entry.
                if (Taxonomy.ResolveRepresentativePathologyId(acronym, allPathologies) is { } id)
                { _rhythmVm.SelectRhythm(id, persist: false, immediate: true); return; }
            }
            // No authored rhythm resolved. Torsades has a recognizable morphology → synthesize a polymorphic-VT
            // trace rather than show a wrong substitute or diverge silently.
            if (TreatmentRhythmMap.IsSynthesizedTorsades(state)) { _rhythmVm.ShowTorsades(); return; }
            // No representative rhythm in the pak for this state (only reachable on a reduced/custom pak). The
            // monitor keeps the previous trace, which would silently contradict the status/log — surface it so the
            // divergence is visible rather than misleading. Skip during initial load (index not yet populated).
            if (_rhythmVm.Rhythms.Count > 0)
                _vm?.LogSystem(AppStrings.TreatmentLogUnresolvedFormat(AppStrings.TreatmentStateName(state)));
        }
        finally { _selfDrivingRhythm = false; }
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private UIElement BuildPanel()
    {
        _picks.Clear(); // buttons from a prior build are discarded; don't keep restyling them
        var root = new Grid { Padding = new Thickness(10, 8, 10, 8) };
        // All rows size to content so the panel is exactly as tall as it needs to be (the host caps it at the
        // available height and scrolls only if it ever overflows) — no forced full-height stretch that would
        // leave empty space between the cards and the log.
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // protocol for this rhythm
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // actions (two columns)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // speed (full width)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // log (compact, fixed inner height)

        // Header: «Лечение» title + preset + Отмена (reset-all, confirmed) + Применить (commit pending effect now).
        var header = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 0, 6) };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var activePreset = _appVm?.TreatmentProtocolStore.GetActivePreset();
        var presetName = activePreset?.Name ?? AppStrings.TxPresetDefault;
        var title = new TextBlock
        {
            Text = $"{AppStrings.TreatmentTitle} — {presetName}",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
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
        if (_onClose is not null)
        {
            var close = new Button { Content = "✕", Padding = new Thickness(9, 4, 9, 4), FontSize = 13 };
            close.Click += (_, _) => _onClose?.Invoke();
            headerButtons.Children.Add(close);
        }
        Grid.SetColumn(headerButtons, 1);
        titleRow.Children.Add(headerButtons);
        header.Children.Add(titleRow);

        _statusText.Foreground = _textPrimary;
        header.Children.Add(_statusText);
        _pendingText.Foreground = AppTheme.Accent;
        header.Children.Add(_pendingText);
        header.Children.Add(_pendingProgressBar);
        header.Children.Add(_pendingCountdownText);
        header.Children.Add(_arrestBanner);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Applicable authored protocol steps for the current rhythm (reference; collapses when none apply).
        _protocolCard = new Border
        {
            Background = _cardBackground,
            BorderBrush = _cardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        var protoStack = new StackPanel { Spacing = 4 };
        protoStack.Children.Add(new TextBlock
        {
            Text = AppStrings.TpPanelProtocols,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
        });
        protoStack.Children.Add(_protocolHost);
        _protocolCard.Child = protoStack;
        Grid.SetRow(_protocolCard, 1);
        root.Children.Add(_protocolCard);

        // Action cards laid out in TWO balanced columns so the whole panel fits without scrolling. (The host
        // card sizes to this content and only scrolls if it ever exceeds the available height.)
        var cardsGrid = new Grid { ColumnSpacing = 6 };
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftCol = new StackPanel { Spacing = 6 };
        var rightCol = new StackPanel { Spacing = 6 };
        leftCol.Children.Add(BuildIvDrugCard());
        leftCol.Children.Add(BuildPacingCard());
        var toggles = new Grid { ColumnSpacing = 6 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var oxy = (FrameworkElement)BuildOxygenCard(); Grid.SetColumn(oxy, 0); toggles.Children.Add(oxy);
        var cpr = (FrameworkElement)BuildCprCard(); Grid.SetColumn(cpr, 1); toggles.Children.Add(cpr);
        leftCol.Children.Add(toggles);
        rightCol.Children.Add(BuildDefibCard());
        rightCol.Children.Add(BuildPillCard());
        rightCol.Children.Add(BuildVagalCard());
        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(rightCol, 1);
        cardsGrid.Children.Add(leftCol);
        cardsGrid.Children.Add(rightCol);
        cardsGrid.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(cardsGrid, 2);
        root.Children.Add(cardsGrid);

        // Accelerated-clock speed spans the full width (a global setting, and it evens out the two columns).
        var speed = (FrameworkElement)BuildSpeedControl();
        speed.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(speed, 3);
        root.Children.Add(speed);

        // Event log.
        var logCard = new Border
        {
            Background = _cardBackground,
            BorderBrush = _cardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 8, 6),
        };
        var logStack = new StackPanel { Spacing = 4 };
        var logHeader = new Grid();
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logTitle = new TextBlock { Text = AppStrings.TxEventLog, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _textPrimary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(logTitle, 0);
        logHeader.Children.Add(logTitle);
        var saveLog = new Button { Content = AppStrings.CommonSave, Padding = new Thickness(10, 2, 10, 2), FontSize = 12 };
        saveLog.Click += (_, _) => _ = SaveLogAsync();
        Grid.SetColumn(saveLog, 1);
        logHeader.Children.Add(saveLog);
        logStack.Children.Add(logHeader);
        logStack.Children.Add(new ScrollViewer { Content = _logHost, Height = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        logCard.Child = logStack;
        Grid.SetRow(logCard, 4);
        root.Children.Add(logCard);

        return root;
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    private UIElement Card(Color bg, string icon, string title, UIElement body, FrameworkElement? action = null)
    {
        var textBrush = bg == Yellow ? Ink : White;
        var head = new Grid { ColumnSpacing = 5 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (action is not null)
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconTb = new TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(iconTb, 0);
        Grid.SetColumn(titleTb, 1);
        head.Children.Add(iconTb);
        head.Children.Add(titleTb);

        if (action is not null)
        {
            Grid.SetColumn(action, 2);
            head.Children.Add(action);
        }

        var headerBorder = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Padding = new Thickness(8, 5, 8, 5),
            Child = head,
        };

        var bodyBorder = new Border
        {
            Padding = new Thickness(8, 7, 8, 8),
            Child = body,
        };

        var cardStack = new StackPanel();
        cardStack.Children.Add(headerBorder);
        cardStack.Children.Add(bodyBorder);

        return new Border
        {
            Background = _cardBackground,
            BorderBrush = _cardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = cardStack,
        };
    }

    private Button PickButton(string text, Color cardBg, Func<bool> isActive, Action onClick)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 3, 4, 3),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        StylePick(btn, isActive(), cardBg);
        btn.Click += (_, _) => { onClick(); RestylePicks(); };
        _picks.Add((btn, cardBg, isActive));
        return btn;
    }

    private static void StylePick(Button btn, bool active, Color cardBg)
    {
        if (active)
        {
            btn.Background = new SolidColorBrush(cardBg);
            btn.BorderBrush = new SolidColorBrush(cardBg);
            if (btn.Content is TextBlock tb)
                tb.Foreground = cardBg == Yellow ? Ink : White;
        }
        else
        {
            btn.Background = new SolidColorBrush(Color.FromArgb(0x18, cardBg.R, cardBg.G, cardBg.B));
            btn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, cardBg.R, cardBg.G, cardBg.B));
            if (btn.Content is TextBlock tb)
                tb.Foreground = AppTheme.AppTextPrimary;
        }
    }

    private void RestylePicks()
    {
        foreach (var (btn, cardBg, active) in _picks) StylePick(btn, active(), cardBg);
    }

    private Button CreateAddDrugButton(bool isPill)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = "+", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = White, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(6, 1, 6, 1),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 6, Width = 220, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = AppStrings.TxAddCustomDrug, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = AppTheme.AppTextPrimary });
        var nameBox = new TextBox { PlaceholderText = AppStrings.TxCustomDrugName, FontSize = 12 };
        panel.Children.Add(nameBox);
        var doseBox = new NumberBox { Value = 1.0, Minimum = 0.1, PlaceholderText = AppStrings.TxCustomDrugDose, SmallChange = 1, FontSize = 12 };
        panel.Children.Add(doseBox);
        var addBtn = new Button { Content = AppStrings.CommonOk, HorizontalAlignment = HorizontalAlignment.Right };
        addBtn.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var dose = double.IsNaN(doseBox.Value) || doseBox.Value <= 0 ? 1.0 : doseBox.Value;
            var item = new CustomDrugItem
            {
                Name = new LocText(name, name),
                IsIv = !isPill,
                DefaultDoseMg = dose,
                MaxDoseMg = dose * 10,
            };
            if (_protocolSet is null) _protocolSet = new();
            _protocolSet.CustomDrugs.Add(item);
            _appVm?.TreatmentProtocolStore.Save(_protocolSet);
            flyout.Hide();
            Content = BuildPanel();
            RefreshStatus();
            RefreshLog();
        };
        panel.Children.Add(addBtn);
        flyout.Content = panel;
        btn.Flyout = flyout;
        return btn;
    }

    private UIElement BuildIvDrugCard()
    {
        var body = new StackPanel { Spacing = 5 };
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var drugs = new[] { TreatmentDrug.Adrenaline, TreatmentDrug.Amiodarone, TreatmentDrug.Atropine, TreatmentDrug.MagnesiumSulfate, TreatmentDrug.CalciumChloride, TreatmentDrug.Adenosine };
        var customIv = _protocolSet?.CustomDrugs.Where(d => d.IsIv).ToList() ?? new();

        var totalCount = drugs.Length + customIv.Count;
        for (var i = 0; i < totalCount; i++)
        {
            Button b;
            if (i < drugs.Length)
            {
                var drug = drugs[i];
                b = PickButton(AppStrings.TreatmentDrugName(drug), Green, () => _selectedDrug == drug && _selectedCustomDrug is null, () =>
                {
                    _selectedDrug = drug;
                    _selectedCustomDrug = null;
                    _doseMg = DrugCatalog.StandardDoseMg(drug);
                    if (_doseBox is not null) _doseBox.Value = _doseMg;
                });
            }
            else
            {
                var cDrug = customIv[i - drugs.Length];
                b = PickButton(cDrug.Name.Pick(IsRussian), Green, () => _selectedCustomDrug == cDrug, () =>
                {
                    _selectedCustomDrug = cDrug;
                    _selectedDrug = null;
                    _doseMg = cDrug.DefaultDoseMg;
                    if (_doseBox is not null) _doseBox.Value = _doseMg;
                });
            }

            Grid.SetRow(b, i / 2); Grid.SetColumn(b, i % 2);
            if (i / 2 >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(b);
        }
        body.Children.Add(grid);

        var doseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        _doseBox = new NumberBox { Value = _selectedDrug is { } sd ? DrugCatalog.StandardDoseMg(sd) : (_selectedCustomDrug?.DefaultDoseMg ?? double.NaN), PlaceholderText = "0", Minimum = 0, SmallChange = 0.5, Width = 78, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _doseBox.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) _doseMg = e.NewValue; };
        FieldFocus.SpinButtonsOnlyWhenFocused(_doseBox);
        doseRow.Children.Add(_doseBox);
        doseRow.Children.Add(new TextBlock { Text = AppStrings.TxUnitMg, Foreground = _textSecondary, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        var give = CardButton(AppStrings.TxBtnGive, Green);
        give.Click += (_, _) =>
        {
            if (_selectedCustomDrug is { } cd)
            {
                var dose = double.IsNaN(_doseMg) || _doseMg <= 0 ? cd.DefaultDoseMg : _doseMg;
                TryApply(new TreatmentAction.Drug(TreatmentDrug.Adrenaline, dose, cd.Name.Pick(IsRussian)));
                return;
            }
            if (_selectedDrug is not { } d) { Toast(AppStrings.TxPickDrug); return; }
            var standardDose = double.IsNaN(_doseMg) || _doseMg <= 0 ? DrugCatalog.StandardDoseMg(d) : _doseMg;
            TryApply(new TreatmentAction.Drug(d, standardDose));
        };
        doseRow.Children.Add(give);
        body.Children.Add(doseRow);

        var plusBtn = CreateAddDrugButton(isPill: false);
        return Card(Green, "💉", AppStrings.TxCardIv, body, plusBtn);
    }

    private UIElement BuildDefibCard()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(SliderRow(AppStrings.TxEnergy, 50, 360, 50, _energy, v => _energy = v, v => $"{v} {AppStrings.TxUnitJoules}", s => _energySlider = s));
        var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        syncRow.Children.Add(new TextBlock { Text = AppStrings.TxSync, Foreground = _textSecondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _syncToggle = new ToggleSwitch { IsOn = _sync, OnContent = null, OffContent = null, MinWidth = 0 };
        _syncToggle.Toggled += (_, _) => _sync = _syncToggle.IsOn;
        syncRow.Children.Add(_syncToggle);
        body.Children.Add(syncRow);
        var shock = new Button
        {
            Content = new TextBlock { Text = AppStrings.TxBtnShock, Foreground = White, FontWeight = FontWeights.Bold },
            Background = new SolidColorBrush(Red),
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
        var body = new StackPanel { Spacing = 5 };
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pills = new[] { TreatmentDrug.Nitroglycerin, TreatmentDrug.Aspirin, TreatmentDrug.Metoprolol };
        var customPills = _protocolSet?.CustomDrugs.Where(d => !d.IsIv).ToList() ?? new();

        var totalCount = pills.Length + customPills.Count;
        for (var i = 0; i < totalCount; i++)
        {
            Button b;
            if (i < pills.Length)
            {
                var pill = pills[i];
                b = PickButton(AppStrings.TreatmentDrugName(pill), Blue, () => _selectedPill == pill && _selectedCustomDrug is null, () =>
                {
                    _selectedPill = pill;
                    _selectedCustomDrug = null;
                });
            }
            else
            {
                var cPill = customPills[i - pills.Length];
                b = PickButton(cPill.Name.Pick(IsRussian), Blue, () => _selectedCustomDrug == cPill, () =>
                {
                    _selectedCustomDrug = cPill;
                    _selectedPill = null;
                });
            }

            Grid.SetRow(b, i / 2); Grid.SetColumn(b, i % 2);
            if (i / 2 >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(b);
        }
        body.Children.Add(grid);
        var give = CardButton(AppStrings.TxBtnGive, Blue);
        give.HorizontalAlignment = HorizontalAlignment.Left;
        give.Click += (_, _) =>
        {
            if (_selectedCustomDrug is { } cd)
            {
                TryApply(new TreatmentAction.Drug(TreatmentDrug.Nitroglycerin, cd.DefaultDoseMg, cd.Name.Pick(IsRussian)));
                return;
            }
            if (_selectedPill is { } p)
            {
                TryApply(new TreatmentAction.Drug(p, DrugCatalog.StandardDoseMg(p)));
            }
            else Toast(AppStrings.TxPickDrug);
        };
        body.Children.Add(give);
        var plusBtn = CreateAddDrugButton(isPill: true);
        return Card(Blue, "💊", AppStrings.TxCardPill, body, plusBtn);
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
        return Card(Orange, "💓", AppStrings.TxCardPacing, body);
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
        _oxyToggle = new ToggleSwitch { IsOn = _vm?.Context.OxygenOn ?? false, OnContent = null, OffContent = null, MinWidth = 0 };
        _oxyToggle.Toggled += (_, _) => { if (!_syncingToggles) TryApply(new TreatmentAction.Oxygen(_oxyToggle.IsOn)); };
        return Card(Cyan, "🌬️", AppStrings.TxCardOxygen, _oxyToggle);
    }

    private void UpdateCprAnimation()
    {
        if (_cprToggle?.IsOn == true)
        {
            _cprAnimText.Visibility = Visibility.Visible;
            _cprProgressBar.Visibility = Visibility.Visible;
            if (_cprAnimTimer is null)
            {
                var dq = DispatcherQueue.GetForCurrentThread();
                if (dq is not null)
                {
                    _cprAnimTimer = dq.CreateTimer();
                    _cprAnimTimer.Interval = TimeSpan.FromMilliseconds(550); // ~110 compressions/min
                    _cprAnimTimer.IsRepeating = true;
                    _cprAnimTimer.Tick += (_, _) =>
                    {
                        if (_cprToggle?.IsOn != true) { StopCprAnimation(); return; }
                        _cprCompressionCount++;
                        if (_cprCompressionCount > 30)
                        {
                            _cprCompressionCount = 1;
                            _cprCycleCount++;
                        }
                        _cprProgressBar.Value = _cprCompressionCount;
                        _cprAnimText.Text = AppStrings.TxCprAnimLabel(_cprCompressionCount, _cprCycleCount);
                    };
                    _cprAnimTimer.Start();
                }
            }
        }
        else
        {
            StopCprAnimation();
        }
    }

    private void StopCprAnimation()
    {
        _cprAnimTimer?.Stop();
        _cprAnimTimer = null;
        _cprCompressionCount = 0;
        _cprCycleCount = 1;
        _cprAnimText.Visibility = Visibility.Collapsed;
        _cprProgressBar.Visibility = Visibility.Collapsed;
    }

    private UIElement BuildCprCard()
    {
        var body = new StackPanel { Spacing = 4 };
        _cprToggle = new ToggleSwitch { IsOn = _vm?.Context.CprActive ?? false, OnContent = null, OffContent = null, MinWidth = 0 };
        _cprToggle.Toggled += (_, _) =>
        {
            if (!_syncingToggles) TryApply(new TreatmentAction.Cpr(_cprToggle.IsOn));
            UpdateCprAnimation();
        };
        body.Children.Add(_cprToggle);
        body.Children.Add(_cprProgressBar);
        body.Children.Add(_cprAnimText);
        UpdateCprAnimation();
        return Card(Pink, "👐", AppStrings.TxCardCpr, body);
    }

    private void SyncToggles()
    {
        if (_vm is null) return;
        _syncingToggles = true;
        if (_oxyToggle is not null) _oxyToggle.IsOn = _vm.Context.OxygenOn;
        if (_cprToggle is not null)
        {
            _cprToggle.IsOn = _vm.Context.CprActive;
            UpdateCprAnimation();
        }
        _syncingToggles = false;
    }

    private UIElement BuildSpeedControl()
    {
        var body = SliderRowThemed(AppStrings.TxSpeed, 10, 240, 10, (int)(_vm?.SpeedFactor ?? 60),
            v => { if (_vm is not null) _vm.SpeedFactor = v; }, v => $"×{v}");
        return new Border
        {
            Background = _subtleFill,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            Child = body,
        };
    }

    private UIElement WithHeader(string title, UIElement body)
    {
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _textPrimary });
        s.Children.Add(body);
        return s;
    }

    private UIElement SliderRow(string label, int min, int max, int step, int value, Action<int> onChange, Func<int, string> fmt, Action<Slider>? capture = null)
    {
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = label, Foreground = _textSecondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var valueText = new TextBlock { Text = fmt(value), Foreground = _textPrimary, FontSize = 11, FontWeight = FontWeights.SemiBold, MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { var v = (int)e.NewValue; onChange(v); valueText.Text = fmt(v); };
        capture?.Invoke(slider);
        Grid.SetColumn(lbl, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(valueText, 2);
        row.Children.Add(lbl); row.Children.Add(slider); row.Children.Add(valueText);
        return row;
    }

    private UIElement SliderRowThemed(string label, int min, int max, int step, int value, Action<int> onChange, Func<int, string> fmt)
    {
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = label, Foreground = _textSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var valueText = new TextBlock { Text = fmt(value), Foreground = _textPrimary, FontSize = 12, FontWeight = FontWeights.SemiBold, MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { var v = (int)e.NewValue; onChange(v); valueText.Text = fmt(v); };
        Grid.SetColumn(lbl, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(valueText, 2);
        row.Children.Add(lbl); row.Children.Add(slider); row.Children.Add(valueText);
        return row;
    }

    private Button CardButton(string text, Color cardBg)
    {
        var textBrush = cardBg == Yellow ? Ink : White;
        return new Button
        {
            Content = new TextBlock { Text = text, Foreground = textBrush, FontSize = 11, FontWeight = FontWeights.SemiBold },
            Background = new SolidColorBrush(cardBg),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
        };
    }

    // ── Header actions (Отмена / Применить) ────────────────────────────────────

    private async System.Threading.Tasks.Task ResetAllAsync()
    {
        if (_vm is null || !await ConfirmAsync(AppStrings.TxConfirmResetAll)) return;
        _selectedDrug = null; _selectedPill = null; _selectedCustomDrug = null; _selectedVagal = null; _doseMg = 0;
        StopCprAnimation();
        if (_doseBox is not null) _doseBox.Value = double.NaN;
        if (_energySlider is not null) _energySlider.Value = 200;       // ValueChanged updates the field + label
        if (_paceRateSlider is not null) _paceRateSlider.Value = 70;
        if (_paceCurrentSlider is not null) _paceCurrentSlider.Value = 50;
        if (_syncToggle is not null) _syncToggle.IsOn = false;
        _vm.Reset();               // clears engine/context/log; fires StateChanged → status/banner/toggles refresh
        SeedFromCurrentRhythm();   // re-seed the engine state from the rhythm still on the monitor
        RestylePicks();            // drop the chip highlights
    }

    // «Применить»: commit any in-progress delayed effect now (skip the accelerated-clock wait). The button is
    // enabled only while an effect is pending, so the toast is just a defensive fallback.
    private void ApplyPending()
    {
        if (_vm is null) return;
        if (!_vm.CommitPendingNow()) Toast(AppStrings.TxNoPending);
    }

    // Save the session event log to a user-chosen text file (mirrors StudentsScreen.OnExportClickAsync — the
    // app's inline FileSavePicker idiom; no picker plumbing is threaded into this screen).
    private async System.Threading.Tasks.Task SaveLogAsync()
    {
        if (_vm is null) return;
        if (_vm.Log.Count == 0) { Toast(AppStrings.TxSaveLogEmpty); return; }
        if (App.MainWindow is not { } window) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = $"treatment_log_{DateTime.Now:yyyyMMdd_HHmmss}",
        };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("Text file (*.txt)", new List<string> { ".txt" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return; // user cancelled
        try
        {
            await FileIO.WriteTextAsync(file, BuildLogReport());
            Toast(AppStrings.TxSaveLogOkFormat(file.Name));
        }
        catch (Exception ex)
        {
            Toast($"{AppStrings.TxSaveLogFailed}: {ex.Message}");
        }
    }

    // Renders the session log as a readable report: a small header (mode, timestamp, final rhythm) then the
    // events in chronological order (the log is stored newest-first, so reverse it).
    private string BuildLogReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppStrings.TreatmentTitle} — {AppStrings.TxEventLog}");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (_vm is not null)
        {
            sb.AppendLine(AppStrings.TxStatusFormat(AppStrings.TreatmentStateName(_vm.CurrentState)));
            sb.AppendLine();
            foreach (var e in _vm.Log.Reverse())
                sb.AppendLine($"{e.Time}  {e.Message}");
        }
        return sb.ToString();
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
            Title = AppStrings.TreatmentTitle,
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
            Title = AppStrings.TreatmentTitle,
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

        if (_vm.HasPendingEffect)
        {
            _pendingProgressBar.Visibility = Visibility.Visible;
            _pendingCountdownText.Visibility = Visibility.Visible;
            var total = Math.Max(0.1, _vm.PendingTotalSeconds);
            var rem = Math.Max(0, _vm.PendingRemainingSeconds);
            var elapsed = Math.Clamp(total - rem, 0, total);
            var pct = (int)Math.Round(100.0 * elapsed / total);
            _pendingProgressBar.Maximum = total;
            _pendingProgressBar.Value = elapsed;
            _pendingCountdownText.Text = AppStrings.TxPendingCountdownFormat(rem.ToString("0.0"), pct);
        }
        else
        {
            _pendingProgressBar.Visibility = Visibility.Collapsed;
            _pendingCountdownText.Visibility = Visibility.Collapsed;
        }

        // «Применить» fast-forwards a pending effect — enabled only while one is in progress.
        if (_applyButton is not null)
        {
            _applyButton.IsEnabled = _vm.HasPendingEffect;
            _applyButton.Opacity = _vm.HasPendingEffect ? 1.0 : 0.5;
        }

        RefreshProtocols();

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

    // Show the authored protocol transitions that apply to the current rhythm (the «show as a guide» half of
    // the feature). Collapses the card when the rhythm has no authored steps.
    private void RefreshProtocols()
    {
        if (_protocolCard is null || _vm is null) return;
        _protocolHost.Children.Clear();

        var applicable = _protocolSet?.Transitions
            .Where(t => t.FromState == _vm.CurrentState)
            .ToList() ?? new System.Collections.Generic.List<Data.TransitionProtocol>();
        if (applicable.Count == 0)
        {
            _protocolCard.Visibility = Visibility.Collapsed;
            return;
        }
        _protocolCard.Visibility = Visibility.Visible;

        var ru = AppStrings.Current == CardioSimulator.Core.Domain.Language.RU;
        foreach (var t in applicable)
        {
            var actions = string.Join(" + ", t.Actions.Select(a => a.Text.Pick(ru)));
            var results = string.Join(" / ", t.Results.Select(r => r.Text.Pick(ru)));
            var cond = t.Conditions.Pick(ru);

            var line = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            { Text = actions, FontWeight = FontWeights.SemiBold, Foreground = _textPrimary });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            { Text = "  →  " + results, Foreground = _textPrimary });
            if (!string.IsNullOrWhiteSpace(cond))
                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                { Text = "   " + cond, Foreground = _textSecondary });
            _protocolHost.Children.Add(line);
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
                TreatmentLogKind.Action => _textPrimary,
                _ => _textSecondary,
            };
            var line = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = color };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.Time + "  ", Foreground = _textSecondary });
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.Message });
            _logHost.Children.Add(line);
        }
    }

}
