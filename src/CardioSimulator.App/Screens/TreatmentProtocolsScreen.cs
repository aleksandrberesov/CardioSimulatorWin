using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Domain.Treatment;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// «Протоколы лечения» — a clinical reference screen AND its editor. It shows four sections (rhythm→action
/// transition table, action-validation rules, a simplified ACLS flowchart with timings, and a standard
/// dosage table) sourced from <see cref="TreatmentProtocolStore"/> (built-in defaults until the Admin
/// edits them). In the Full edition under the Admin role a header "Edit protocols" toggle reveals
/// Add/Edit/Delete/reorder controls; every change is persisted immediately. Text is authored as bilingual
/// EN/RU pairs and displayed in the active language with English fallback, matching the app's localization
/// contract. Students and the Limited edition never see the edit controls.
///
/// The whole page rebuilds on theme change, on role change (Admin↔User), and after every edit; language
/// changes rebuild it from <c>MainScreen.BuildForMode</c>.
/// </summary>
public sealed class TreatmentProtocolsScreen : UserControl
{
    // ── Badge palette (from the reference mock-up; saturated fills read on light and dark alike) ──
    private static readonly Color RhythmNormalColor = Rgb(0x2D, 0x50, 0x16);
    private static readonly Color RhythmDangerColor = Rgb(0x7A, 0x24, 0x24);
    private static readonly Color RhythmWarningColor = Rgb(0x7A, 0x4A, 0x15);
    private static readonly Color ActionMedColor = Rgb(0x4A, 0x7C, 0x23);
    private static readonly Color ActionElecColor = Rgb(0xC3, 0x33, 0x33);
    private static readonly Color ActionMechColor = Rgb(0x3A, 0x7C, 0xA5);
    private static readonly Color ActionVagalColor = Rgb(0xC7, 0xC5, 0x3A);
    private static readonly Color ArrowGreen = Rgb(0x5C, 0x9C, 0x2E);
    private static readonly Color White = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Black = Rgb(0x00, 0x00, 0x00);

    private readonly ScrollViewer _root = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private AppViewModel? _appVm;
    private TreatmentProtocolStore? _store;
    private TreatmentProtocolSet _set = new();
    private bool _editRequested;

    public TreatmentProtocolsScreen()
    {
        Content = _root;
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += OnUnloaded;
    }

    public void Initialize(AppViewModel appVm)
    {
        _appVm = appVm;
        _store = appVm.TreatmentProtocolStore;
        _set = _store.Load();
        appVm.PropertyChanged += OnAppChanged;
        BuildPage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppTheme.Changed -= OnThemeChanged;
        if (_appVm is not null) _appVm.PropertyChanged -= OnAppChanged;
    }

    private void OnThemeChanged() => BuildPage();

    private void OnAppChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Leaving Admin must hide the edit controls immediately (the screen instance persists across an
        // Admin↔User switch made from the Settings dialog).
        if (e.PropertyName == nameof(AppViewModel.Role))
        {
            if (!CanEdit) _editRequested = false;
            BuildPage();
        }
    }

    private bool CanEdit => _appVm?.Role == AppRole.Admin;
    private bool Editing => _editRequested && CanEdit;
    private static bool Ru => AppStrings.Current == CardioSimulator.Core.Domain.Language.RU;
    private static string P(LocText t) => t.Pick(Ru);

    private void Persist() => _store?.Save(_set);

    private void PersistAndRebuild()
    {
        Persist();
        BuildPage();
    }

    // ── Page ────────────────────────────────────────────────────────────────
    private void BuildPage()
    {
        _root.Background = AppTheme.AppPageBackground;
        var offset = _root.VerticalOffset;

        var stack = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(20),
            MaxWidth = 1400,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildLegend());
        stack.Children.Add(Card(AppStrings.TpSectionTransitions, BuildTransitionsTable(),
            () => _ = EditTransitionAsync(null)));
        stack.Children.Add(Card(AppStrings.TpSectionRules, BuildRules(),
            () => _ = EditRuleAsync(null)));
        stack.Children.Add(Card(AppStrings.TpSectionAcls, BuildAcls(),
            () => _ = EditAclsAsync(null)));
        stack.Children.Add(Card(AppStrings.TpSectionDosages, BuildDosagesTable(),
            () => _ = EditDosageAsync(null)));

        _root.Content = stack;

        if (offset > 0)
        {
            void OnLoaded(object s, RoutedEventArgs e)
            {
                stack.Loaded -= OnLoaded;
                _root.ChangeView(null, offset, null, true);
            }
            stack.Loaded += OnLoaded;
        }
    }

    private UIElement BuildHeader()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.ModeName(OperatingMode.TreatmentProtocols),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
        });
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.TpIntro,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.AppTextSecondary,
        });

        if (CanEdit)
        {
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var editToggle = new Button
            {
                Content = Editing ? AppStrings.TpEditDone : AppStrings.TpEditMode,
            };
            if (Editing)
            {
                editToggle.Background = new SolidColorBrush(ArrowGreen);
                editToggle.Foreground = new SolidColorBrush(White);
            }
            editToggle.Click += (_, _) => { _editRequested = !_editRequested; BuildPage(); };
            toolbar.Children.Add(editToggle);

            if (Editing)
            {
                var reset = new Button { Content = AppStrings.TpReset };
                reset.Click += (_, _) => _ = ResetAsync();
                toolbar.Children.Add(reset);

                toolbar.Children.Add(new TextBlock
                {
                    Text = AppStrings.TpEditHint,
                    Foreground = AppTheme.AppTextSecondary,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            panel.Children.Add(toolbar);
        }

        return panel;
    }

    private UIElement BuildLegend()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = AppStrings.TpLegend + ":",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.AppTextSecondary,
            FontSize = 12,
        });
        row.Children.Add(Badge(AppStrings.TpCategoryMed, ActionMedColor, White));
        row.Children.Add(Badge(AppStrings.TpCategoryElec, ActionElecColor, White));
        row.Children.Add(Badge(AppStrings.TpCategoryMech, ActionMechColor, White));
        row.Children.Add(Badge(AppStrings.TpCategoryVagal, ActionVagalColor, Black));

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = row,
        };
    }

    // ── Section 1: rhythm transition table ────────────────────────────────────
    private UIElement BuildTransitionsTable()
    {
        var headers = new List<string>
        {
            AppStrings.TpColCurrent, AppStrings.TpColAction, AppStrings.TpColResult,
            AppStrings.TpColTime, AppStrings.TpColConditions,
        };
        var widths = new List<double> { 1.6, 1.7, 1.7, 1.0, 1.6 };
        if (Editing) { headers.Add(string.Empty); widths.Add(1.1); }

        var rows = new List<UIElement[]>();
        var list = _set.Transitions;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var cells = new List<UIElement>
            {
                RhythmBadge(t.CurrentKind, P(t.Current)),
                ActionStack(t.Actions),
                ResultStack(t.Results),
                CellText(P(t.Time)),
                CellText(P(t.Conditions)),
            };
            if (Editing) cells.Add(RowControls(list, i, t, () => EditTransitionAsync(t)));
            rows.Add(cells.ToArray());
        }
        return WrapScroll(BuildTable(headers, rows, widths, 900), 900);
    }

    // ── Section 2: validation rules ───────────────────────────────────────────
    private UIElement BuildRules()
    {
        var panel = new StackPanel { Spacing = 8 };
        var list = _set.Rules;
        if (Editing && list.Count == 0) panel.Children.Add(EmptyNote());
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            FrameworkElement item = RuleItem(P(r.Lead), P(r.Body));
            if (Editing) item = WithRowControls(item, list, i, r, () => EditRuleAsync(r));
            panel.Children.Add(item);
        }
        return panel;
    }

    private static Border RuleItem(string lead, string body)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = lead + " ",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ArrowGreen),
        });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = body,
            Foreground = AppTheme.AppTextPrimary,
        });

        return new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(ArrowGreen),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Child = text,
        };
    }

    // ── Section 3: ACLS flowchart + timings ────────────────────────────────────
    private UIElement BuildAcls()
    {
        var panel = new StackPanel { Spacing = 14 };

        var flow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var steps = _set.AclsSteps;
        if (Editing && steps.Count == 0) flow.Children.Add(EmptyNote());
        for (int i = 0; i < steps.Count; i++)
        {
            flow.Children.Add(FlowNode(steps, i));
            if (i < steps.Count - 1)
                flow.Children.Add(new TextBlock
                {
                    Text = "→",
                    FontSize = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(ArrowGreen),
                });
        }

        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = flow,
        });

        // Timings box.
        var timings = new StackPanel { Spacing = 3 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock
        {
            Text = AppStrings.TpTimings,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (Editing)
        {
            var addTiming = SmallButton("＋", AppStrings.TpAdd);
            addTiming.Click += (_, _) => _ = EditTimingAsync(null);
            header.Children.Add(addTiming);
        }
        timings.Children.Add(header);

        var tlist = _set.Timings;
        for (int i = 0; i < tlist.Count; i++)
        {
            var line = tlist[i];
            var lineText = new TextBlock
            {
                Text = "• " + P(line.Text),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppTheme.AppTextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Editing)
                timings.Children.Add(WithRowControls(lineText, tlist, i, line, () => EditTimingAsync(line)));
            else
                timings.Children.Add(lineText);
        }

        panel.Children.Add(new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = timings,
        });

        return panel;
    }

    private UIElement FlowNode(List<AclsStep> steps, int index)
    {
        var n = steps[index];
        var (border, back) = n.Kind switch
        {
            AclsNodeKind.Critical => (ActionElecColor, (Color?)WithAlpha(ActionElecColor, 0x33)),
            AclsNodeKind.Normal => (RhythmNormalColor, (Color?)WithAlpha(ActionMedColor, 0x33)),
            _ => (ActionMechColor, (Color?)null),
        };

        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock
        {
            Text = P(n.Title),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = AppTheme.AppTextPrimary,
            TextAlignment = TextAlignment.Center,
        });
        var sub = P(n.Subtitle);
        if (!string.IsNullOrEmpty(sub))
            inner.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Foreground = AppTheme.AppTextSecondary,
                TextAlignment = TextAlignment.Center,
            });

        if (Editing)
        {
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            controls.Children.Add(MoveButton("◀", () => Move(steps, index, -1)));
            controls.Children.Add(EditButton(() => EditAclsAsync(n)));
            controls.Children.Add(DeleteButton(() => Delete(steps, index)));
            controls.Children.Add(MoveButton("▶", () => Move(steps, index, +1)));
            inner.Children.Add(controls);
        }

        return new Border
        {
            MinWidth = 140,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(border),
            Background = new SolidColorBrush(back ?? AppTheme.AppSubtleFillColor),
            Child = inner,
        };
    }

    // ── Section 4: dosages table ────────────────────────────────────────────────
    private UIElement BuildDosagesTable()
    {
        var headers = new List<string>
        {
            AppStrings.TpColDrug, AppStrings.TpColIndication, AppStrings.TpColDose,
            AppStrings.TpColRoute, AppStrings.TpColRepeat,
        };
        var widths = new List<double> { 1.2, 1.8, 1.6, 1.2, 1.6 };
        if (Editing) { headers.Add(string.Empty); widths.Add(1.1); }

        var rows = new List<UIElement[]>();
        var list = _set.Dosages;
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            var cells = new List<UIElement>
            {
                CellText(P(d.Drug), bold: true),
                CellText(P(d.Indication)),
                CellText(P(d.Dose)),
                CellText(P(d.Route)),
                CellText(P(d.Repeat)),
            };
            if (Editing) cells.Add(RowControls(list, i, d, () => EditDosageAsync(d)));
            rows.Add(cells.ToArray());
        }
        return WrapScroll(BuildTable(headers, rows, widths, 820), 820);
    }

    // ── Reorder / delete (works on any list via IList) ──────────────────────────
    private void Move(IList list, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= list.Count) return;
        var tmp = list[index];
        list[index] = list[target];
        list[target] = tmp;
        PersistAndRebuild();
    }

    private void Delete(IList list, int index)
    {
        if (index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
        PersistAndRebuild();
    }

    // ── Edit dialogs ────────────────────────────────────────────────────────────
    private async Task EditTransitionAsync(TransitionProtocol? existing)
    {
        var working = existing?.Clone() ?? new TransitionProtocol();
        var panel = new StackPanel { Spacing = 4, MinWidth = 560 };

        var kindCombo = EnumCombo(new[] { AppStrings.TpKindNormal, AppStrings.TpKindDanger, AppStrings.TpKindWarning }, (int)working.CurrentKind);
        AddLabeled(panel, AppStrings.TpFieldKind, kindCombo);
        var curField = AddLocField(panel, AppStrings.TpColCurrent, working.Current);

        // ── Simulator binding — drives the Лечение panel in Teaching (leave "— none —" for a display-only row).
        panel.Children.Add(SectionCaption(AppStrings.TpEngineSection));
        var fromStateCombo = StateCombo(working.FromState);
        AddLabeled(panel, AppStrings.TpFieldFromState, fromStateCombo);
        var triggerCombo = EnumCombo(TriggerLabels, (int)working.Trigger);
        AddLabeled(panel, AppStrings.TpFieldTrigger, triggerCombo);
        var drugCombo = DrugCombo(working.TriggerDrug);
        AddLabeled(panel, AppStrings.TpTrigDrug, drugCombo);
        void SyncDrugEnabled() => drugCombo.IsEnabled = triggerCombo.SelectedIndex == (int)TransitionTrigger.Drug;
        triggerCombo.SelectionChanged += (_, _) => SyncDrugEnabled();
        SyncDrugEnabled();
        var effectBox = new NumberBox
        {
            Value = working.EffectSeconds,
            Minimum = 0,
            Maximum = 100000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AddLabeled(panel, AppStrings.TpFieldEffect, effectBox);

        panel.Children.Add(SectionCaption(AppStrings.TpActions));
        var actionsHost = new StackPanel { Spacing = 6 };
        var actionRows = new List<BadgeRow>();
        panel.Children.Add(actionsHost);
        foreach (var a in working.Actions) AddBadgeRow(actionsHost, actionRows, ActionCategoryLabels, (int)a.Category, a.Text);
        panel.Children.Add(AddInDialogButton(AppStrings.TpActions, () => AddBadgeRow(actionsHost, actionRows, ActionCategoryLabels, 0, null)));

        panel.Children.Add(SectionCaption(AppStrings.TpResults));
        var resultsHost = new StackPanel { Spacing = 6 };
        var resultRows = new List<ResultRow>();
        panel.Children.Add(resultsHost);
        foreach (var r in working.Results) AddResultRow(resultsHost, resultRows, r);
        panel.Children.Add(AddInDialogButton(AppStrings.TpResults, () => AddResultRow(resultsHost, resultRows, null)));

        var timeField = AddLocField(panel, AppStrings.TpColTime, working.Time);
        var condField = AddLocField(panel, AppStrings.TpColConditions, working.Conditions, multiline: true);

        if (!await ShowDialogAsync(AppStrings.TpDlgTransition, panel)) return;

        working.CurrentKind = (RhythmKind)kindCombo.SelectedIndex;
        working.Current = curField.Read();
        working.FromState = StateFromCombo(fromStateCombo);
        working.Trigger = (TransitionTrigger)triggerCombo.SelectedIndex;
        working.TriggerDrug = triggerCombo.SelectedIndex == (int)TransitionTrigger.Drug && drugCombo.SelectedIndex >= 0
            ? DrugValues[drugCombo.SelectedIndex]
            : (TreatmentDrug?)null;
        working.EffectSeconds = double.IsNaN(effectBox.Value) ? 0 : (int)System.Math.Round(effectBox.Value);
        working.Actions = actionRows.Where(r => !r.IsEmpty)
            .Select(r => new ActionItem { Category = (ActionCategory)r.Combo.SelectedIndex, Text = r.Read() }).ToList();
        working.Results = resultRows.Where(r => !r.IsEmpty).Select(r => r.Read()).ToList();
        working.Time = timeField.Read();
        working.Conditions = condField.Read();

        Upsert(_set.Transitions, existing, working, working.Id, t => t.Id);
        PersistAndRebuild();
    }

    private async Task EditRuleAsync(ValidationRule? existing)
    {
        var working = existing?.Clone() ?? new ValidationRule();
        var panel = new StackPanel { Spacing = 4, MinWidth = 520 };
        var lead = AddLocField(panel, AppStrings.TpRuleLead, working.Lead);
        var body = AddLocField(panel, AppStrings.TpRuleBody, working.Body, multiline: true);
        if (!await ShowDialogAsync(AppStrings.TpDlgRule, panel)) return;
        working.Lead = lead.Read();
        working.Body = body.Read();
        Upsert(_set.Rules, existing, working, working.Id, r => r.Id);
        PersistAndRebuild();
    }

    private async Task EditAclsAsync(AclsStep? existing)
    {
        var working = existing?.Clone() ?? new AclsStep();
        var panel = new StackPanel { Spacing = 4, MinWidth = 480 };
        var kindCombo = EnumCombo(new[] { AppStrings.TpNodeStep, AppStrings.TpNodeCritical, AppStrings.TpNodeSuccess }, (int)working.Kind);
        AddLabeled(panel, AppStrings.TpFieldKind, kindCombo);
        var title = AddLocField(panel, AppStrings.TpStepTitle, working.Title);
        var sub = AddLocField(panel, AppStrings.TpStepSubtitle, working.Subtitle);
        if (!await ShowDialogAsync(AppStrings.TpDlgStep, panel)) return;
        working.Kind = (AclsNodeKind)kindCombo.SelectedIndex;
        working.Title = title.Read();
        working.Subtitle = sub.Read();
        Upsert(_set.AclsSteps, existing, working, working.Id, s => s.Id);
        PersistAndRebuild();
    }

    private async Task EditTimingAsync(TimingLine? existing)
    {
        var working = existing?.Clone() ?? new TimingLine();
        var panel = new StackPanel { Spacing = 4, MinWidth = 480 };
        var text = AddLocField(panel, AppStrings.TpTimingText, working.Text, multiline: true);
        if (!await ShowDialogAsync(AppStrings.TpDlgTiming, panel)) return;
        working.Text = text.Read();
        Upsert(_set.Timings, existing, working, working.Id, t => t.Id);
        PersistAndRebuild();
    }

    private async Task EditDosageAsync(DosageEntry? existing)
    {
        var working = existing?.Clone() ?? new DosageEntry();
        var panel = new StackPanel { Spacing = 4, MinWidth = 520 };
        var drug = AddLocField(panel, AppStrings.TpColDrug, working.Drug);
        var ind = AddLocField(panel, AppStrings.TpColIndication, working.Indication);
        var dose = AddLocField(panel, AppStrings.TpColDose, working.Dose);
        var route = AddLocField(panel, AppStrings.TpColRoute, working.Route);
        var repeat = AddLocField(panel, AppStrings.TpColRepeat, working.Repeat);
        if (!await ShowDialogAsync(AppStrings.TpDlgDosage, panel)) return;
        working.Drug = drug.Read();
        working.Indication = ind.Read();
        working.Dose = dose.Read();
        working.Route = route.Read();
        working.Repeat = repeat.Read();
        Upsert(_set.Dosages, existing, working, working.Id, d => d.Id);
        PersistAndRebuild();
    }

    private async Task ResetAsync()
    {
        if (_store is null) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.TpReset,
            Content = new TextBlock { Text = AppStrings.TpConfirmReset, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        void OnTheme() => dialog.RequestedTheme = AppTheme.Current;
        AppTheme.Changed += OnTheme;
        dialog.Closed += (_, _) => AppTheme.Changed -= OnTheme;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _set = _store.ResetToDefaults();
            BuildPage();
        }
    }

    /// <summary>Replaces the matching item (when editing) or appends the new one (when adding).</summary>
    private static void Upsert<T>(List<T> list, T? existing, T working, string id, System.Func<T, string> idOf) where T : class
    {
        if (existing is null) { list.Add(working); return; }
        var idx = list.FindIndex(x => idOf(x) == id);
        if (idx >= 0) list[idx] = working; else list.Add(working);
    }

    // ── Dialog plumbing ──────────────────────────────────────────────────────────
    private async Task<bool> ShowDialogAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 560,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = AppStrings.CommonSave,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;
        void OnTheme() => dialog.RequestedTheme = AppTheme.Current;
        AppTheme.Changed += OnTheme;
        dialog.Closed += (_, _) => AppTheme.Changed -= OnTheme;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string[] ActionCategoryLabels => new[]
        { AppStrings.TpCategoryMed, AppStrings.TpCategoryElec, AppStrings.TpCategoryMech, AppStrings.TpCategoryVagal };
    private static string[] RhythmKindLabels => new[]
        { AppStrings.TpKindNormal, AppStrings.TpKindDanger, AppStrings.TpKindWarning };

    // Edit fields show ONLY the active locale (RU → Russian, otherwise English — matching the display's
    // English fallback). The other language's stored value is carried through unchanged on save, so editing
    // in one language never wipes the other.
    private static string LocDisplay(LocText? v, bool ru) => v is null ? string.Empty : (ru ? v.Ru : v.En);

    private static LocText ReadLoc(TextBox box, string otherEn, string otherRu, bool ru) =>
        ru ? new LocText(otherEn, box.Text.Trim()) : new LocText(box.Text.Trim(), otherRu);

    private sealed class BadgeRow
    {
        public ComboBox Combo = null!;
        public TextBox Box = null!;
        public string OtherEn = string.Empty;
        public string OtherRu = string.Empty;
        public bool Ru;
        public FrameworkElement Container = null!;
        public LocText Read() => ReadLoc(Box, OtherEn, OtherRu, Ru);
        public bool IsEmpty { get { var t = Read(); return string.IsNullOrWhiteSpace(t.En) && string.IsNullOrWhiteSpace(t.Ru); } }
    }

    private void AddBadgeRow(Panel host, List<BadgeRow> rows, string[] labels, int selected, LocText? value)
    {
        var grid = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var row = new BadgeRow
        {
            Combo = EnumCombo(labels, selected),
            Box = new TextBox { Text = LocDisplay(value, Ru) },
            OtherEn = value?.En ?? string.Empty,
            OtherRu = value?.Ru ?? string.Empty,
            Ru = Ru,
            Container = grid,
        };
        var remove = SmallButton("✕", AppStrings.CommonDelete);
        remove.Click += (_, _) => { host.Children.Remove(grid); rows.Remove(row); };

        Grid.SetColumn(row.Combo, 0);
        Grid.SetColumn(row.Box, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(row.Combo);
        grid.Children.Add(row.Box);
        grid.Children.Add(remove);

        host.Children.Add(grid);
        rows.Add(row);
    }

    private sealed class LocField
    {
        public TextBox Box = null!;
        public string OtherEn = string.Empty;
        public string OtherRu = string.Empty;
        public bool Ru;
        public LocText Read() => ReadLoc(Box, OtherEn, OtherRu, Ru);
    }

    private LocField AddLocField(Panel host, string label, LocText? value, bool multiline = false)
    {
        host.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
            Margin = new Thickness(0, 8, 0, 2),
        });

        var box = new TextBox
        {
            Text = LocDisplay(value, Ru),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (multiline) box.MinHeight = 60;
        host.Children.Add(box);
        return new LocField { Box = box, OtherEn = value?.En ?? string.Empty, OtherRu = value?.Ru ?? string.Empty, Ru = Ru };
    }

    private static void AddLabeled(Panel host, string label, FrameworkElement control)
    {
        host.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
            Margin = new Thickness(0, 8, 0, 2),
        });
        host.Children.Add(control);
    }

    private static TextBlock SectionCaption(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = AppTheme.AppTextPrimary,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static Button AddInDialogButton(string what, System.Action onClick)
    {
        var btn = new Button
        {
            Content = "＋ " + AppStrings.TpAdd,
            Margin = new Thickness(0, 4, 0, 0),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static ComboBox EnumCombo(string[] labels, int selected)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var l in labels) cb.Items.Add(new ComboBoxItem { Content = l });
        cb.SelectedIndex = selected >= 0 && selected < labels.Length ? selected : 0;
        return cb;
    }

    // ── Simulator-binding combos (engine states / drugs / triggers) ─────────────
    private static readonly ClinicalRhythmState[] StateValues = System.Enum.GetValues<ClinicalRhythmState>();
    private static readonly TreatmentDrug[] DrugValues = System.Enum.GetValues<TreatmentDrug>();
    private static string[] TriggerLabels => new[]
    {
        AppStrings.TpTrigNone, AppStrings.TpTrigDefib, AppStrings.TpTrigCardiovert,
        AppStrings.TpTrigDrug, AppStrings.TpTrigPacing, AppStrings.TpTrigVagal,
    };

    // Index 0 = "— none —" (display-only); 1.. = the states in enum order.
    private static ComboBox StateCombo(ClinicalRhythmState? selected)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        cb.Items.Add(new ComboBoxItem { Content = AppStrings.TpStateNone });
        foreach (var s in StateValues) cb.Items.Add(new ComboBoxItem { Content = AppStrings.TreatmentStateName(s) });
        cb.SelectedIndex = selected is { } st ? System.Array.IndexOf(StateValues, st) + 1 : 0;
        return cb;
    }

    private static ClinicalRhythmState? StateFromCombo(ComboBox cb) =>
        cb.SelectedIndex <= 0 ? (ClinicalRhythmState?)null : StateValues[cb.SelectedIndex - 1];

    private static ComboBox DrugCombo(TreatmentDrug? selected)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in DrugValues) cb.Items.Add(new ComboBoxItem { Content = AppStrings.TreatmentDrugName(d) });
        cb.SelectedIndex = selected is { } dd ? System.Array.IndexOf(DrugValues, dd) : 0;
        return cb;
    }

    private static FrameworkElement LabeledColumn(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = AppTheme.AppTextSecondary });
        sp.Children.Add(control);
        return sp;
    }

    // A result row in the transition dialog: display (kind + active-locale text) plus the engine binding
    // (→ rhythm + chance). The inactive language is carried through unchanged on save.
    private sealed class ResultRow
    {
        public ComboBox Kind = null!;
        public TextBox Box = null!;
        public string OtherEn = string.Empty;
        public string OtherRu = string.Empty;
        public bool Ru;
        public ComboBox State = null!;
        public NumberBox Weight = null!;
        public FrameworkElement Container = null!;
        public LocText ReadText() => ReadLoc(Box, OtherEn, OtherRu, Ru);
        public bool IsEmpty { get { var t = ReadText(); return string.IsNullOrWhiteSpace(t.En) && string.IsNullOrWhiteSpace(t.Ru); } }
        public ResultItem Read() => new()
        {
            Kind = (RhythmKind)Kind.SelectedIndex,
            Text = ReadText(),
            State = StateFromCombo(State),
            Weight = double.IsNaN(Weight.Value) ? 1 : Weight.Value,
        };
    }

    private void AddResultRow(Panel host, List<ResultRow> rows, ResultItem? value)
    {
        var box = new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
        };
        var stack = new StackPanel { Spacing = 6 };

        var g1 = new Grid { ColumnSpacing = 6 };
        g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var row = new ResultRow
        {
            Kind = EnumCombo(RhythmKindLabels, value is null ? 0 : (int)value.Kind),
            Box = new TextBox { Text = LocDisplay(value?.Text, Ru) },
            OtherEn = value?.Text.En ?? string.Empty,
            OtherRu = value?.Text.Ru ?? string.Empty,
            Ru = Ru,
            State = StateCombo(value?.State),
            Weight = new NumberBox
            {
                Value = value?.Weight ?? 1,
                Minimum = 0,
                Maximum = 1,
                SmallChange = 0.05,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            },
            Container = box,
        };
        var remove = SmallButton("✕", AppStrings.CommonDelete);
        remove.Click += (_, _) => { host.Children.Remove(box); rows.Remove(row); };
        Grid.SetColumn(row.Kind, 0);
        Grid.SetColumn(row.Box, 1);
        Grid.SetColumn(remove, 2);
        g1.Children.Add(row.Kind);
        g1.Children.Add(row.Box);
        g1.Children.Add(remove);

        var g2 = new Grid { ColumnSpacing = 6 };
        g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var stateCol = LabeledColumn(AppStrings.TpFieldResultState, row.State);
        var weightCol = LabeledColumn(AppStrings.TpFieldWeight, row.Weight);
        Grid.SetColumn(stateCol, 0);
        Grid.SetColumn(weightCol, 1);
        g2.Children.Add(stateCol);
        g2.Children.Add(weightCol);

        stack.Children.Add(g1);
        stack.Children.Add(g2);
        box.Child = stack;
        host.Children.Add(box);
        rows.Add(row);
    }

    // ── Row controls (edit / reorder / delete) ──────────────────────────────────
    private StackPanel RowControls<T>(IList list, int index, T item, System.Func<Task> editAsync)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(MoveButton("▲", () => Move(list, index, -1)));
        panel.Children.Add(MoveButton("▼", () => Move(list, index, +1)));
        panel.Children.Add(EditButton(() => editAsync()));
        panel.Children.Add(DeleteButton(() => Delete(list, index)));
        return panel;
    }

    private Grid WithRowControls<T>(FrameworkElement content, IList list, int index, T item, System.Func<Task> editAsync)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        var controls = RowControls(list, index, item, editAsync);
        controls.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(controls, 1);
        grid.Children.Add(content);
        grid.Children.Add(controls);
        return grid;
    }

    private static Button EditButton(System.Func<Task> onClick)
    {
        var b = SmallButton("✎", AppStrings.CommonEdit);
        b.Click += (_, _) => _ = onClick();
        return b;
    }

    private Button DeleteButton(System.Action onClick)
    {
        var b = SmallButton("🗑", AppStrings.CommonDelete);
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button MoveButton(string glyph, System.Action onClick)
    {
        var b = SmallButton(glyph, null);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button SmallButton(string glyph, string? tooltip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 13 },
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    private static UIElement EmptyNote() => new TextBlock
    {
        Text = AppStrings.TpEmpty,
        Foreground = AppTheme.AppTextSecondary,
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        FontSize = 13,
    };

    // ── Shared building blocks ──────────────────────────────────────────────────
    private Border Card(string title, UIElement body, System.Action? onAdd)
    {
        var stack = new StackPanel { Spacing = 12 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        headerRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ArrowGreen),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (Editing && onAdd is not null)
        {
            var add = new Button { Content = "＋ " + AppStrings.TpAdd };
            add.Click += (_, _) => onAdd();
            headerRow.Children.Add(add);
        }
        stack.Children.Add(headerRow);

        stack.Children.Add(new Border
        {
            Height = 2,
            Background = AppTheme.AppCardBorder,
            Margin = new Thickness(0, -4, 0, 0),
        });
        stack.Children.Add(body);

        return new Border
        {
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Child = stack,
        };
    }

    private static UIElement WrapScroll(FrameworkElement table, double minWidth)
    {
        table.MinWidth = minWidth;
        table.HorizontalAlignment = HorizontalAlignment.Stretch;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = table,
        };
    }

    private static Grid BuildTable(IReadOnlyList<string> headers, List<UIElement[]> rows, IReadOnlyList<double> starWidths, double minWidth)
    {
        var grid = new Grid { MinWidth = minWidth };
        for (int c = 0; c < headers.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(starWidths[c], GridUnitType.Star) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Count; c++)
        {
            var cell = TableCell(new TextBlock
            {
                Text = headers[c],
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.AppTextPrimary,
                TextWrapping = TextWrapping.Wrap,
            }, header: true);
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Count && c < rows[r].Length; c++)
            {
                var cell = TableCell(rows[r][c], header: false);
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private static Border TableCell(UIElement content, bool header) => new()
    {
        Padding = new Thickness(10, 8, 10, 8),
        Background = header ? AppTheme.AppSubtleFill : null,
        BorderBrush = AppTheme.AppCardBorder,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Child = content,
    };

    private static TextBlock CellText(string text, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = AppTheme.AppTextPrimary,
    };

    private UIElement RhythmBadge(RhythmKind kind, string text) => Badge(text, RhythmColor(kind), White);

    private UIElement ActionStack(IReadOnlyList<ActionItem> actions)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var a in actions)
        {
            var (bg, fg) = a.Category == ActionCategory.Vagal ? (ActionVagalColor, Black) : (ActionColor(a.Category), White);
            panel.Children.Add(Badge(P(a.Text), bg, fg));
        }
        return panel;
    }

    private UIElement ResultStack(IReadOnlyList<ResultItem> results)
    {
        var wrap = new StackPanel { Spacing = 2 };
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0)
                wrap.Children.Add(new TextBlock
                {
                    Text = Ru ? "или" : "or",
                    FontSize = 11,
                    Foreground = AppTheme.AppTextSecondary,
                });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = "→",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ArrowGreen),
            });
            row.Children.Add(Badge(P(results[i].Text), RhythmColor(results[i].Kind), White));
            wrap.Children.Add(row);
        }
        return wrap;
    }

    private static Border Badge(string text, Color background, Color foreground) => new()
    {
        Background = new SolidColorBrush(background),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(foreground),
        },
    };

    private static Color RhythmColor(RhythmKind k) => k switch
    {
        RhythmKind.Normal => RhythmNormalColor,
        RhythmKind.Danger => RhythmDangerColor,
        _ => RhythmWarningColor,
    };

    private static Color ActionColor(ActionCategory k) => k switch
    {
        ActionCategory.Med => ActionMedColor,
        ActionCategory.Elec => ActionElecColor,
        ActionCategory.Mech => ActionMechColor,
        _ => ActionVagalColor,
    };

    private static Color Rgb(byte r, byte g, byte b) => new() { A = 0xFF, R = r, G = g, B = b };
    private static Color WithAlpha(Color c, byte a) => new() { A = a, R = c.R, G = c.G, B = c.B };
}
