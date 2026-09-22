using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

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
    // The page is deliberately monochrome (the customer found the mock-up's colour badges a hard-to-read "traffic
    // light"): theme text/border brushes only, with AppTheme.Negative as the single accent for critical items.
    private const char WarningSign = '⚠';

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
        stack.Children.Add(Card(AppStrings.TpSectionTransitions, BuildTransitionsTable(),
            () => _ = EditTransitionAsync(null)));
        // Authoring-only: the custom-drug list is an editor for what the Лечение panel offers, not reference
        // content, so it is folded away outside edit mode.
        if (Editing)
            stack.Children.Add(Card(AppStrings.TpSectionCustomDrugs, BuildCustomDrugsTable(),
                () => _ = EditCustomDrugAsync(null)));
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
        var panel = new StackPanel { Spacing = 6 };
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

        if (_store is not null)
        {
            var container = _store.LoadContainer();
            var presetRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            presetRow.Children.Add(new TextBlock
            {
                Text = AppStrings.TxPresetLabel,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.AppTextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var presetCombo = new ComboBox
            {
                MinWidth = 280,
                VerticalAlignment = VerticalAlignment.Center,
            };

            for (var i = 0; i < container.Presets.Count; i++)
            {
                var p = container.Presets[i];
                presetCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                if (p.Id == container.ActivePresetId)
                    presetCombo.SelectedIndex = i;
            }

            if (presetCombo.SelectedIndex < 0 && presetCombo.Items.Count > 0)
                presetCombo.SelectedIndex = 0;

            presetCombo.SelectionChanged += (_, _) =>
            {
                if (presetCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string pid)
                {
                    if (pid != _store.GetActivePreset().Id)
                    {
                        _store.SetActivePresetId(pid);
                        _set = _store.Load();
                        BuildPage();
                    }
                }
            };
            presetRow.Children.Add(presetCombo);

            if (CanEdit)
            {
                var saveAsBtn = new Button { Content = AppStrings.TxPresetSaveAs };
                saveAsBtn.Click += (_, _) => _ = SaveAsPresetAsync();
                presetRow.Children.Add(saveAsBtn);

                var exportBtn = new Button { Content = AppStrings.TxPresetExport };
                exportBtn.Click += (_, _) => _ = ExportPresetAsync();
                presetRow.Children.Add(exportBtn);

                var importBtn = new Button { Content = AppStrings.TxPresetImport };
                importBtn.Click += (_, _) => _ = ImportPresetAsync();
                presetRow.Children.Add(importBtn);

                var active = _store.GetActivePreset();
                if (!active.IsBuiltIn && container.Presets.Count > 1)
                {
                    var deleteBtn = new Button
                    {
                        Content = AppStrings.TxPresetDelete,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                    };
                    deleteBtn.Click += (_, _) => _ = DeletePresetAsync(active.Id);
                    presetRow.Children.Add(deleteBtn);
                }
            }
            panel.Children.Add(presetRow);
        }

        if (CanEdit)
        {
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var editToggle = new Button
            {
                Content = Editing ? AppStrings.TpEditDone : AppStrings.TpEditMode,
            };
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

    // ── Section 1: rhythm transition table ────────────────────────────────────
    // Deliberately calm: plain text, one rhythm cell per group of rows about the same current rhythm, and a
    // single accent (AppTheme.Negative) kept for what must not be missed — a thin bar beside critical rhythms
    // and the ⚠ marker on erroneous actions. The mock-up's per-category / per-kind colour badges read as a
    // "traffic light" and were dropped: the action category is a tooltip, a dangerous result is semibold.
    private UIElement BuildTransitionsTable()
    {
        var headers = new List<string>
        {
            AppStrings.TpColCurrent, AppStrings.TpColAction, AppStrings.TpColResult,
            AppStrings.TpColTime, AppStrings.TpColConditions,
        };
        var widths = new List<double> { 1.5, 1.8, 1.7, 0.9, 1.7 };
        if (Editing) { headers.Add(string.Empty); widths.Add(1.1); }

        var grid = new Grid();
        foreach (var w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Count; c++)
            PlaceCell(grid, TableCell(HeaderText(headers[c]), header: true), 0, c);

        var list = _set.Transitions;
        var gridRow = 1;
        foreach (var (start, count) in GroupTransitions(list))
        {
            var members = list.GetRange(start, count);
            for (int k = 0; k < count; k++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // The group is titled by its fullest name (repeats are abbreviated: "ФЖ" under "Фибрилляция желудочков (ФЖ)").
            var title = members.Select(t => P(t.Current)).OrderByDescending(s => s.Length).First();
            var critical = members.Any(t => t.CurrentKind == RhythmKind.Danger);
            var rhythmCell = RhythmGroupCell(title, critical);
            PlaceCell(grid, rhythmCell, gridRow, 0);
            Grid.SetRowSpan(rhythmCell, count);

            for (int k = 0; k < count; k++)
            {
                var t = members[k];
                var index = start + k;
                // Hairlines inside a group, a full-strength rule between groups.
                var line = k == count - 1 ? AppTheme.AppCardBorder : AppTheme.AppSubtleFill;
                PlaceCell(grid, BodyCell(ActionLines(t.Actions), line), gridRow + k, 1);
                PlaceCell(grid, BodyCell(ResultLines(t.Results), line), gridRow + k, 2);
                PlaceCell(grid, BodyCell(CellText(P(t.Time)), line), gridRow + k, 3);
                PlaceCell(grid, BodyCell(ConditionText(P(t.Conditions)), line), gridRow + k, 4);
                if (Editing)
                    PlaceCell(grid, BodyCell(RowControls(list, index, t, () => EditTransitionAsync(t)), line), gridRow + k, 5);
            }
            gridRow += count;
        }
        return WrapScroll(grid, 900);
    }

    /// <summary>Splits the transitions into runs of consecutive rows about the same current rhythm. Bound rows
    /// match by engine state; a display-only row (no <see cref="TransitionProtocol.FromState"/>) joins the run
    /// when its rhythm text equals a member's or is the abbreviation in a member's parentheses.</summary>
    private static List<(int Start, int Count)> GroupTransitions(List<TransitionProtocol> list)
    {
        var groups = new List<(int Start, int Count)>();
        for (int i = 0; i < list.Count; i++)
        {
            if (groups.Count > 0)
            {
                var (start, count) = groups[^1];
                if (SameRhythm(list.GetRange(start, count), list[i]))
                {
                    groups[^1] = (start, count + 1);
                    continue;
                }
            }
            groups.Add((i, 1));
        }
        return groups;
    }

    private static bool SameRhythm(List<TransitionProtocol> group, TransitionProtocol row)
    {
        if (!string.IsNullOrWhiteSpace(row.FromPathologyId) &&
            group.Any(t => string.Equals(t.FromPathologyId, row.FromPathologyId, System.StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(row.FromAcronym) &&
            group.Any(t => string.Equals(t.FromAcronym, row.FromAcronym, System.StringComparison.OrdinalIgnoreCase)))
            return true;

        var state = group.Select(t => t.FromState).FirstOrDefault(s => s is not null);
        if (row.FromState is not null && state is not null) return row.FromState == state;

        var text = P(row.Current).Trim();
        if (text.Length == 0) return false;
        return group.Select(t => P(t.Current).Trim()).Any(m =>
            string.Equals(m, text, System.StringComparison.OrdinalIgnoreCase)
            || m.Contains("(" + text + ")", System.StringComparison.OrdinalIgnoreCase)
            || text.Contains("(" + m + ")", System.StringComparison.OrdinalIgnoreCase));
    }

    private static void PlaceCell(Grid grid, FrameworkElement cell, int row, int column)
    {
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Border RhythmGroupCell(string title, bool critical)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new Border
        {
            Background = critical ? AppTheme.Negative : null,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 8, 0, 8),
        });

        var text = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.AppTextPrimary,
            Margin = new Thickness(10, 9, 10, 9),
        };
        if (critical) ToolTipService.SetToolTip(text, AppStrings.TpKindDanger);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private static Border BodyCell(UIElement content, Brush line) => new()
    {
        Padding = new Thickness(10, 9, 10, 9),
        BorderBrush = line,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Child = content,
    };

    // Actions done together stack as "+"-joined lines (as in the Лечение panel's protocol list).
    private static StackPanel ActionLines(IReadOnlyList<ActionItem> actions)
    {
        var panel = new StackPanel { Spacing = 3 };
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var text = PlainText();
            if (i > 0) text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "+ ", Foreground = AppTheme.AppTextSecondary });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = P(a.Text) });
            var labels = ActionCategoryLabels;
            if ((int)a.Category >= 0 && (int)a.Category < labels.Length)
                ToolTipService.SetToolTip(text, labels[(int)a.Category]);
            panel.Children.Add(text);
        }
        return panel;
    }

    // Alternative outcomes read on as one phrase: "→ ЖТ", "или Синусовый ритм".
    private static StackPanel ResultLines(IReadOnlyList<ResultItem> results)
    {
        var panel = new StackPanel { Spacing = 3 };
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var text = PlainText();
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = i == 0 ? "→ " : (Ru ? "или " : "or "),
                Foreground = AppTheme.AppTextSecondary,
            });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = P(r.Text),
                FontWeight = r.Kind == RhythmKind.Danger ? FontWeights.SemiBold : FontWeights.Normal,
            });
            panel.Children.Add(text);
        }
        return panel;
    }

    // An authored leading "⚠" marks an erroneous / ineffective action: the sign is drawn as a monochrome glyph
    // in the alert colour (not the yellow emoji) and the note is set in semibold.
    private static TextBlock ConditionText(string condition)
    {
        var text = PlainText();
        var trimmed = condition.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != WarningSign)
        {
            text.Text = condition;
            return text;
        }

        text.IsColorFontEnabled = false;
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = WarningSign + " ",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            Foreground = AppTheme.Negative,
        });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = trimmed.TrimStart(WarningSign, '️', ' '),
            FontWeight = FontWeights.SemiBold,
        });
        return text;
    }

    private static TextBlock PlainText() => new()
    {
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.AppTextPrimary,
    };

    // ── Section 1b: authored custom drugs ─────────────────────────────────────
    // The regional / protocol-specific drugs an instructor adds on top of the built-in catalog. They show up
    // as extra chips in the Лечение panel's IV and tablet cards, and a transition can be bound to one (see
    // the trigger-drug combo) so a custom drug moves the rhythm exactly like a catalog drug does.
    private UIElement BuildCustomDrugsTable()
    {
        var list = _set.CustomDrugs;
        if (list.Count == 0) return EmptyNote();

        var headers = new List<string>
        {
            AppStrings.TpColDrug, AppStrings.TpColRoute, AppStrings.TpFieldDefaultDose,
            AppStrings.TpFieldMaxDose, string.Empty,
        };
        var widths = new List<double> { 2.0, 1.0, 1.2, 1.2, 1.1 };

        var rows = new List<UIElement[]>();
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            var index = i;
            var unit = UnitOf(d);
            rows.Add(new UIElement[]
            {
                CellText(CustomDrugLabel(d), bold: true),
                CellText(d.IsIv ? AppStrings.TpRouteIv : AppStrings.TpRoutePill),
                CellText($"{Num(d.DefaultDoseMg)} {unit}"),
                CellText(d.MaxDoseMg is { } max ? $"{Num(max)} {unit}" : "—"),
                RowControls(list, index, d, () => EditCustomDrugAsync(d), () => _ = DeleteCustomDrugAsync(index)),
            });
        }
        return WrapScroll(BuildTable(headers, rows, widths, 720), 720);
    }

    /// <summary>The drug's name in the active locale; a nameless entry (only reachable from a hand-edited
    /// file) falls back to a short form of its id so the row is still identifiable.</summary>
    private static string CustomDrugLabel(CustomDrugItem d)
    {
        var name = P(d.Name);
        return string.IsNullOrWhiteSpace(name)
            ? "#" + (d.Id.Length > 6 ? d.Id[..6] : d.Id)
            : name;
    }

    private static string UnitOf(CustomDrugItem d) =>
        string.IsNullOrWhiteSpace(d.Unit) ? AppStrings.TxUnitMg : d.Unit.Trim();

    /// <summary>Doses are authored as doubles but are usually whole numbers — drop the noise zeros.</summary>
    private static string Num(double value) => value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

    // ── Section 2: validation rules ───────────────────────────────────────────
    // Same calm treatment as the transition table: no fills or coloured markers, rules separated by hairlines.
    private UIElement BuildRules()
    {
        var panel = new StackPanel();
        var list = _set.Rules;
        if (Editing && list.Count == 0) panel.Children.Add(EmptyNote());
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            FrameworkElement item = RuleItem(P(r.Lead), P(r.Body));
            if (Editing) item = WithRowControls(item, list, i, r, () => EditRuleAsync(r));
            panel.Children.Add(new Border
            {
                BorderBrush = AppTheme.AppSubtleFill,
                BorderThickness = new Thickness(0, 0, 0, i < list.Count - 1 ? 1 : 0),
                Child = item,
            });
        }
        return panel;
    }

    // Lead and body in two columns whose proportions match the transition table's rhythm column, so the leads
    // line up under "Current rhythm". The authored trailing colon is redundant in a column and isn't shown.
    private static Grid RuleItem(string lead, string body)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6.1, GridUnitType.Star) });

        var leadText = PlainText();
        leadText.Text = lead.TrimEnd().TrimEnd(':');
        leadText.FontWeight = FontWeights.SemiBold;
        leadText.Margin = new Thickness(10, 9, 10, 9);

        var bodyText = PlainText();
        bodyText.Text = body;
        bodyText.Margin = new Thickness(10, 9, 10, 9);
        Grid.SetColumn(bodyText, 1);

        grid.Children.Add(leadText);
        grid.Children.Add(bodyText);
        return grid;
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
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AppTheme.AppTextSecondary,
                });
        }

        panel.Children.Add(HScroll(flow));

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
            var lineText = PlainText();
            lineText.Text = "• " + P(line.Text);
            lineText.VerticalAlignment = VerticalAlignment.Center;
            if (Editing)
                timings.Children.Add(WithRowControls(lineText, tlist, i, line, () => EditTimingAsync(line)));
            else
                timings.Children.Add(lineText);
        }

        panel.Children.Add(new Border
        {
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = AppTheme.MediumCornerRadius,
            Padding = new Thickness(12),
            Child = timings,
        });

        return panel;
    }

    private UIElement FlowNode(List<AclsStep> steps, int index)
    {
        var n = steps[index];
        // Neutral boxes; only the critical entry node keeps the alert accent (like the table's critical-rhythm
        // bar). Emoji in authored text ("✅ Success") are drawn monochrome so no stray colour creeps back in.
        var critical = n.Kind == AclsNodeKind.Critical;

        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock
        {
            Text = P(n.Title),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = AppTheme.AppTextPrimary,
            TextAlignment = TextAlignment.Center,
            IsColorFontEnabled = false,
        });
        var sub = P(n.Subtitle);
        if (!string.IsNullOrEmpty(sub))
            inner.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                Foreground = AppTheme.AppTextSecondary,
                TextAlignment = TextAlignment.Center,
                IsColorFontEnabled = false,
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
            CornerRadius = AppTheme.MediumCornerRadius,
            BorderThickness = new Thickness(critical ? 1.5 : 1),
            BorderBrush = critical ? AppTheme.Negative : AppTheme.AppCardBorder,
            Background = AppTheme.AppCardBackground,
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
        var fromChoice = new RhythmChoiceControl(
            _appVm?.Repository.Pathologies() ?? new List<PathologyEntry>(),
            Ru,
            working.FromState,
            working.FromPathologyId,
            working.FromAcronym,
            isResult: false);

        fromChoice.TitleResolved += title =>
        {
            if (title is not null && string.IsNullOrWhiteSpace(curField.Box.Text))
            {
                curField.Box.Text = Ru ? title.Ru : title.En;
                curField.OtherEn = title.En;
                curField.OtherRu = title.Ru;
            }
        };

        var fromGrid = new Grid();
        fromGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fromGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(fromChoice.Container, 0);
        fromGrid.Children.Add(fromChoice.Container);

        var fillBtn = SmallButton("🪄 " + AppStrings.TpAutoFillTitle, AppStrings.TpAutoFillTitle);
        fillBtn.Margin = new Thickness(6, 0, 0, 0);
        fillBtn.Click += (_, _) =>
        {
            fromChoice.Commit();
            if (fromChoice.LocalizedTitle is { } title)
            {
                curField.Box.Text = Ru ? title.Ru : title.En;
                curField.OtherEn = title.En;
                curField.OtherRu = title.Ru;
            }
        };
        Grid.SetColumn(fillBtn, 1);
        fromGrid.Children.Add(fillBtn);

        AddLabeled(panel, AppStrings.TpFieldFromState, fromGrid);

        var triggerCombo = EnumCombo(TriggerLabels, (int)working.Trigger);
        AddLabeled(panel, AppStrings.TpFieldTrigger, triggerCombo);
        var drugPick = DrugCombo(working.TriggerDrug, working.TriggerCustomDrugId);
        AddLabeled(panel, AppStrings.TpTrigDrug, drugPick.Combo);
        void SyncDrugEnabled() => drugPick.Combo.IsEnabled = triggerCombo.SelectedIndex == (int)TransitionTrigger.Drug;
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
        fromChoice.Commit();
        working.FromState = fromChoice.DerivedState;
        working.FromPathologyId = fromChoice.SelectedPathologyId;
        working.FromAcronym = fromChoice.SelectedAcronym;
        working.Trigger = (TransitionTrigger)triggerCombo.SelectedIndex;
        var drugTrigger = triggerCombo.SelectedIndex == (int)TransitionTrigger.Drug;
        // Exactly one of the two is set: a custom drug binds by its stable id, a catalog drug by its enum.
        working.TriggerDrug = drugTrigger ? drugPick.Drug : null;
        working.TriggerCustomDrugId = drugTrigger ? drugPick.CustomDrugId : null;
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

    private async Task EditCustomDrugAsync(CustomDrugItem? existing)
    {
        var working = existing?.Clone() ?? new CustomDrugItem();
        var panel = new StackPanel { Spacing = 4, MinWidth = 480 };
        var name = AddLocField(panel, AppStrings.TpColDrug, working.Name);
        var routeCombo = EnumCombo(new[] { AppStrings.TpRouteIv, AppStrings.TpRoutePill }, working.IsIv ? 0 : 1);
        AddLabeled(panel, AppStrings.TpColRoute, routeCombo);

        var doseBox = new NumberBox
        {
            Value = working.DefaultDoseMg,
            Minimum = 0,
            Maximum = 100000,
            SmallChange = 0.5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AddLabeled(panel, AppStrings.TpFieldDefaultDose, doseBox);

        var unitBox = new TextBox { Text = working.Unit ?? string.Empty, PlaceholderText = AppStrings.TxUnitMg };
        AddLabeled(panel, AppStrings.TpFieldUnit, unitBox);

        // Left blank = no cap (CustomDrugItem.MaxDoseMg is nullable); NumberBox reports that as NaN.
        var maxBox = new NumberBox
        {
            Value = working.MaxDoseMg ?? double.NaN,
            Minimum = 0,
            Maximum = 1000000,
            SmallChange = 0.5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AddLabeled(panel, AppStrings.TpFieldMaxDose, maxBox);

        if (!await ShowDialogAsync(AppStrings.TpDlgCustomDrug, panel)) return;

        var read = name.Read();
        // A nameless drug would be an unlabelled chip in the Лечение panel — don't create one.
        if (string.IsNullOrWhiteSpace(read.En) && string.IsNullOrWhiteSpace(read.Ru)) return;

        working.Name = read;
        working.IsIv = routeCombo.SelectedIndex == 0;
        working.DefaultDoseMg = double.IsNaN(doseBox.Value) || doseBox.Value <= 0 ? 1.0 : doseBox.Value;
        // Left blank stays blank, so the table renders the unit in whatever language is active.
        working.Unit = unitBox.Text?.Trim() ?? string.Empty;
        working.MaxDoseMg = double.IsNaN(maxBox.Value) || maxBox.Value <= 0 ? null : maxBox.Value;

        Upsert(_set.CustomDrugs, existing, working, working.Id, d => d.Id);
        PersistAndRebuild();
    }

    /// <summary>Deleting a custom drug also unbinds every transition that fired on it: the trigger drops to
    /// "none" (the row stays as reference text) rather than silently re-pointing at a standard drug.</summary>
    private async Task DeleteCustomDrugAsync(int index)
    {
        if (index < 0 || index >= _set.CustomDrugs.Count) return;
        var drug = _set.CustomDrugs[index];
        var bound = _set.Transitions
            .Where(t => string.Equals(t.TriggerCustomDrugId, drug.Id, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        var message = AppStrings.TpConfirmDeleteCustomDrugFormat(CustomDrugLabel(drug));
        if (bound.Count > 0) message += "\n\n" + AppStrings.TpCustomDrugBoundFormat(bound.Count);
        if (!await ConfirmAsync(AppStrings.CommonDelete, message)) return;

        foreach (var t in bound)
        {
            t.TriggerCustomDrugId = null;
            t.Trigger = TransitionTrigger.None;
        }
        _set.CustomDrugs.RemoveAt(index);
        PersistAndRebuild();
    }

    private async Task ResetAsync()
    {
        if (_store is null) return;
        if (!await ConfirmAsync(AppStrings.TpReset, AppStrings.TpConfirmReset)) return;
        _set = _store.ResetToDefaults();
        BuildPage();
    }

    private async Task SaveAsPresetAsync()
    {
        if (_store is null) return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        var nameBox = new TextBox { PlaceholderText = AppStrings.TxPresetNamePrompt };
        var descBox = new TextBox { PlaceholderText = AppStrings.TpPresetDescriptionHint, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 60 };
        AddLabeled(panel, AppStrings.TxPresetNamePrompt, nameBox);
        AddLabeled(panel, AppStrings.TpPresetDescription, descBox);

        if (!await ShowDialogAsync(AppStrings.TxPresetSaveAs, panel)) return;
        var name = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.SaveAsNewPreset(name, descBox.Text.Trim(), _set);
        _set = _store.Load();
        BuildPage();
    }

    // Export/import go through the system file dialogs (a preset is shared as a .json file); the clipboard
    // remains the secondary path, for pasting a preset into a chat or a ticket. The picker idiom
    // (hwnd-initialised FileSavePicker) mirrors TreatmentPanel.SaveLogAsync.
    private async Task ExportPresetAsync()
    {
        if (_store is null) return;
        var active = _store.GetActivePreset();
        var json = _store.ExportPresetJson(active.Id);

        var dialog = MessageDialog(AppStrings.TxPresetExport, AppStrings.TpExportPromptFormat(active.Name));
        dialog.PrimaryButtonText = AppStrings.TpSaveToFile;
        dialog.SecondaryButtonText = AppStrings.TpCopyClipboard;
        dialog.CloseButtonText = AppStrings.CommonCancel;
        dialog.DefaultButton = ContentDialogButton.Primary;

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await SavePresetFileAsync(active, json);
                break;
            case ContentDialogResult.Secondary:
                var package = new DataPackage();
                package.SetText(json);
                Clipboard.SetContent(package);
                await InfoAsync(AppStrings.TxPresetExport, AppStrings.TpCopiedToClipboard);
                break;
        }
    }

    private async Task SavePresetFileAsync(TreatmentProtocolPreset preset, string json)
    {
        if (App.MainWindow is not { } window) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeFileName(preset.Name),
        };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add(AppStrings.TpJsonFileType, new List<string> { ".json" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return; // user cancelled
        try
        {
            // UTF-8 with no BOM, matching what the store writes (FileIO.WriteTextAsync would add one).
            await FileIO.WriteBytesAsync(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
            await InfoAsync(AppStrings.TxPresetExport, AppStrings.TpExportOkFormat(file.Name));
        }
        catch (System.Exception ex)
        {
            await InfoAsync(AppStrings.TxPresetExport, $"{AppStrings.TpExportFailed}: {ex.Message}");
        }
    }

    /// <summary>A preset name is free text (it can hold guillemets, a number sign, a slash) -- swap out
    /// whatever Windows rejects in a file name.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "treatment-preset" : cleaned;
    }

    private async Task ImportPresetAsync()
    {
        if (_store is null) return;

        var dialog = MessageDialog(AppStrings.TxPresetImport, AppStrings.TpImportPrompt);
        dialog.PrimaryButtonText = AppStrings.TpOpenFile;
        dialog.SecondaryButtonText = AppStrings.TpPasteJson;
        dialog.CloseButtonText = AppStrings.CommonCancel;
        dialog.DefaultButton = ContentDialogButton.Primary;

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await ImportFromFileAsync();
                break;
            case ContentDialogResult.Secondary:
                await ImportFromPastedJsonAsync();
                break;
        }
    }

    private async Task ImportFromFileAsync()
    {
        if (App.MainWindow is not { } window) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return; // user cancelled
        string json;
        try { json = await FileIO.ReadTextAsync(file); }
        catch (System.Exception ex) { await InfoAsync(AppStrings.TpImportError, ex.Message); return; }
        await ApplyImportAsync(json);
    }

    private async Task ImportFromPastedJsonAsync()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 500 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.TpPasteJsonHint,
            Foreground = AppTheme.AppTextSecondary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        var textBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 240,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            PlaceholderText = "{\n  \"name\": \"...\",\n  \"protocolSet\": { ... }\n}",
        };
        panel.Children.Add(textBox);

        if (!await ShowDialogAsync(AppStrings.TxPresetImport, panel)) return;
        await ApplyImportAsync(textBox.Text);
    }

    private async Task ApplyImportAsync(string json)
    {
        if (_store is null) return;
        if (string.IsNullOrWhiteSpace(json)) return;

        if (_store.ImportPresetJson(json.Trim(), out _, out var error))
        {
            _set = _store.Load();
            BuildPage();
            await InfoAsync(AppStrings.TxPresetImport, AppStrings.TpImportOkFormat(_store.GetActivePreset().Name));
        }
        else
        {
            await InfoAsync(AppStrings.TpImportError, string.IsNullOrWhiteSpace(error) ? AppStrings.TpImportBadJson : error);
        }
    }

    private async Task DeletePresetAsync(string presetId)
    {
        if (_store is null) return;
        if (!await ConfirmAsync(AppStrings.TxPresetDelete, AppStrings.TpConfirmDeletePreset)) return;
        _store.DeletePreset(presetId);
        _set = _store.Load();
        BuildPage();
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

    /// <summary>A themed OK/Cancel confirmation (every destructive action on this screen asks the same way).</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = MessageDialog(title, message);
        dialog.PrimaryButtonText = AppStrings.CommonOk;
        dialog.CloseButtonText = AppStrings.CommonCancel;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>A themed one-button notice (import/export outcomes and errors).</summary>
    private async Task InfoAsync(string title, string message)
    {
        var dialog = MessageDialog(title, message);
        dialog.CloseButtonText = AppStrings.CommonOk;
        await dialog.ShowAsync();
    }

    // Theme doesn't reach a ContentDialog on its own, and a long-lived dialog must follow a live theme change.
    private ContentDialog MessageDialog(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        void OnTheme() => dialog.RequestedTheme = AppTheme.Current;
        AppTheme.Changed += OnTheme;
        dialog.Closed += (_, _) => AppTheme.Changed -= OnTheme;
        return dialog;
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

    /// <summary>The trigger-drug combo's selection, read back as either a catalog drug or a custom-drug id.
    /// Entries are the <see cref="DrugValues"/> in enum order, then the set's custom drugs in list order.</summary>
    private sealed class DrugPick
    {
        public ComboBox Combo = null!;
        /// <summary>Custom-drug ids, positionally matching the entries appended after the standard drugs.</summary>
        public List<string> CustomIds = null!;

        private int CustomIndex => Combo.SelectedIndex - DrugValues.Length;
        public string? CustomDrugId => CustomIndex >= 0 && CustomIndex < CustomIds.Count ? CustomIds[CustomIndex] : null;
        public TreatmentDrug? Drug => CustomIndex >= 0 ? null
            : Combo.SelectedIndex >= 0 && Combo.SelectedIndex < DrugValues.Length ? DrugValues[Combo.SelectedIndex] : null;
    }

    /// <summary>Builds the trigger-drug combo: the standard catalog drugs followed by the protocol set's own
    /// custom drugs, so an authored transition can fire on a regional drug the instructor added. A custom
    /// drug is stored by its stable id, so renaming it keeps the binding; an id the set no longer defines is
    /// still listed (marked with the warning sign) so editing another field never silently rebinds the row to
    /// a standard drug.</summary>
    private DrugPick DrugCombo(TreatmentDrug? selected, string? customDrugId)
    {
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in DrugValues) cb.Items.Add(new ComboBoxItem { Content = AppStrings.TreatmentDrugName(d) });

        var ids = new List<string>();
        foreach (var c in _set.CustomDrugs)
        {
            cb.Items.Add(new ComboBoxItem { Content = CustomDrugLabel(c) });
            ids.Add(c.Id);
        }
        if (!string.IsNullOrEmpty(customDrugId) &&
            !ids.Any(id => string.Equals(id, customDrugId, System.StringComparison.OrdinalIgnoreCase)))
        {
            cb.Items.Add(new ComboBoxItem { Content = $"{WarningSign} {customDrugId}" });
            ids.Add(customDrugId);
        }

        var custom = string.IsNullOrEmpty(customDrugId)
            ? -1
            : ids.FindIndex(id => string.Equals(id, customDrugId, System.StringComparison.OrdinalIgnoreCase));
        cb.SelectedIndex = custom >= 0
            ? DrugValues.Length + custom
            : selected is { } dd ? System.Array.IndexOf(DrugValues, dd) : 0;
        return new DrugPick { Combo = cb, CustomIds = ids };
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
    private sealed class RhythmChoiceControl
    {
        public FrameworkElement Container { get; }
        public ComboBox ModeCombo { get; }
        public AutoSuggestBox AcronymBox { get; }
        public AutoSuggestBox RealRhythmBox { get; }

        public string? SelectedAcronym { get; private set; }
        public string? SelectedPathologyId { get; private set; }
        public ClinicalRhythmState? DerivedState { get; private set; }
        public LocText? LocalizedTitle { get; private set; }

        public event System.Action<LocText?>? TitleResolved;

        public RhythmChoiceControl(
            IReadOnlyList<PathologyEntry> pathologies,
            bool ru,
            ClinicalRhythmState? initialState,
            string? initialPathologyId,
            string? initialAcronym,
            bool isResult)
        {
            var allPathologies = new List<PathologyEntry>(pathologies);
            if (!allPathologies.Any(p => p.Id == PathologyEntry.SyntheticAsystole.Id))
                allPathologies.Add(PathologyEntry.SyntheticAsystole);
            if (!allPathologies.Any(p => p.Id == PathologyEntry.SyntheticTorsades.Id))
                allPathologies.Add(PathologyEntry.SyntheticTorsades);

            var modes = isResult
                ? new[] { AppStrings.TpModeAcronym, AppStrings.TpModeRealRhythm }
                : new[] { AppStrings.TpModeAcronym, AppStrings.TpModeRealRhythm, AppStrings.TpModeDisplayOnly };

            ModeCombo = EnumCombo(modes, 0);
            ModeCombo.MinWidth = isResult ? 120 : 145;

            AcronymBox = new AutoSuggestBox
            {
                PlaceholderText = AppStrings.TpAcronymSearchPrompt,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            RealRhythmBox = new AutoSuggestBox
            {
                PlaceholderText = AppStrings.TpRealRhythmSearchPrompt,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
            };

            // Acronym search wire-up
            AcronymBox.TextChanged += (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var needle = AcronymBox.Text.Trim();
                AcronymBox.ItemsSource = Taxonomy.Shared.Entries
                    .Where(x => needle.Length == 0
                        || x.Acronym.Contains(needle, System.StringComparison.OrdinalIgnoreCase)
                        || x.NameRu.Contains(needle, System.StringComparison.OrdinalIgnoreCase)
                        || x.NameEn.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .Select(x => $"{x.Acronym} — {(ru ? x.NameRu : x.NameEn)}")
                    .ToList();
            };

            void ApplyAcronymCode(string code)
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    SelectedAcronym = null;
                    DerivedState = null;
                    LocalizedTitle = null;
                    return;
                }
                var entry = Taxonomy.Shared.Find(code);
                if (entry is not null)
                {
                    SelectedAcronym = entry.Acronym;
                    AcronymBox.Text = $"{entry.Acronym} — {(ru ? entry.NameRu : entry.NameEn)}";
                    DerivedState = TreatmentRhythmMap.ClassifyByAcronyms(new[] { entry.Acronym });
                    LocalizedTitle = new LocText(entry.NameEn, entry.NameRu);
                }
                else
                {
                    SelectedAcronym = code.ToUpperInvariant();
                    DerivedState = TreatmentRhythmMap.ClassifyByAcronyms(new[] { SelectedAcronym });
                    LocalizedTitle = new LocText(SelectedAcronym, SelectedAcronym);
                }
                SelectedPathologyId = null;
                TitleResolved?.Invoke(LocalizedTitle);
            }

            AcronymBox.SuggestionChosen += (_, args) =>
            {
                if (args.SelectedItem is string s && s.Contains(" — "))
                {
                    var code = s.Split(" — ")[0].Trim();
                    ApplyAcronymCode(code);
                }
            };

            AcronymBox.QuerySubmitted += (_, args) =>
            {
                var token = (args.ChosenSuggestion as string) ?? AcronymBox.Text;
                var code = token.Contains(" — ") ? token.Split(" — ")[0].Trim() : token.Trim();
                ApplyAcronymCode(code);
            };

            // Real rhythm search wire-up
            RealRhythmBox.TextChanged += (_, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var needle = RealRhythmBox.Text.Trim();
                RealRhythmBox.ItemsSource = allPathologies
                    .Where(p => needle.Length == 0
                        || p.Id.Contains(needle, System.StringComparison.OrdinalIgnoreCase)
                        || (p.Number.HasValue && p.Number.Value.ToString().Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                        || (p.TitleEn?.Contains(needle, System.StringComparison.OrdinalIgnoreCase) ?? false)
                        || (p.ResolvedNameRu?.Contains(needle, System.StringComparison.OrdinalIgnoreCase) ?? false)
                        || p.AcronymList.Any(a => a.Contains(needle, System.StringComparison.OrdinalIgnoreCase)))
                    .Take(15)
                    .Select(p => $"{(p.Number.HasValue ? $"№{p.Number} " : "")}{p.Id} — {(ru ? p.ResolvedNameRu ?? p.TitleEn : p.TitleEn)}")
                    .ToList();
            };

            void ApplyPathologyToken(string token)
            {
                var rawId = token;
                if (rawId.Contains(" — ")) rawId = rawId.Split(" — ")[0].Trim();
                if (rawId.StartsWith("№") && rawId.Contains(" ")) rawId = rawId.Substring(rawId.IndexOf(' ') + 1).Trim();
                rawId = rawId.Trim();

                var p = allPathologies.FirstOrDefault(x => string.Equals(x.Id, rawId, System.StringComparison.OrdinalIgnoreCase)
                    || (x.Number.HasValue && int.TryParse(rawId, out var num) && x.Number.Value == num));

                if (p is not null)
                {
                    SelectedPathologyId = p.Id;
                    SelectedAcronym = p.AcronymList.FirstOrDefault();
                    DerivedState = TreatmentRhythmMap.ClassifyByAcronyms(p.AcronymList);
                    RealRhythmBox.Text = $"{(p.Number.HasValue ? $"№{p.Number} " : "")}{p.Id} — {(ru ? p.ResolvedNameRu ?? p.TitleEn : p.TitleEn)}";
                    LocalizedTitle = new LocText(p.TitleEn, p.ResolvedNameRu ?? p.TitleEn);
                }
                else if (!string.IsNullOrWhiteSpace(rawId))
                {
                    SelectedPathologyId = rawId;
                    SelectedAcronym = null;
                    DerivedState = null;
                    LocalizedTitle = new LocText(rawId, rawId);
                }
                else
                {
                    SelectedPathologyId = null;
                    SelectedAcronym = null;
                    DerivedState = null;
                    LocalizedTitle = null;
                }
                TitleResolved?.Invoke(LocalizedTitle);
            }

            RealRhythmBox.SuggestionChosen += (_, args) =>
            {
                if (args.SelectedItem is string s)
                    ApplyPathologyToken(s);
            };

            RealRhythmBox.QuerySubmitted += (_, args) =>
            {
                var token = (args.ChosenSuggestion as string) ?? RealRhythmBox.Text;
                ApplyPathologyToken(token);
            };

            // Mode switching
            ModeCombo.SelectionChanged += (_, _) =>
            {
                var idx = ModeCombo.SelectedIndex;
                if (idx == 0) // Acronym
                {
                    AcronymBox.Visibility = Visibility.Visible;
                    RealRhythmBox.Visibility = Visibility.Collapsed;
                    var text = AcronymBox.Text.Trim();
                    var code = text.Contains(" — ") ? text.Split(" — ")[0].Trim() : text;
                    ApplyAcronymCode(code);
                }
                else if (idx == 1) // Real rhythm
                {
                    AcronymBox.Visibility = Visibility.Collapsed;
                    RealRhythmBox.Visibility = Visibility.Visible;
                    ApplyPathologyToken(RealRhythmBox.Text);
                }
                else // Display only
                {
                    AcronymBox.Visibility = Visibility.Collapsed;
                    RealRhythmBox.Visibility = Visibility.Collapsed;
                    SelectedAcronym = null;
                    SelectedPathologyId = null;
                    DerivedState = null;
                    LocalizedTitle = null;
                    TitleResolved?.Invoke(null);
                }
            };

            // Initialize values
            if (!string.IsNullOrWhiteSpace(initialPathologyId))
            {
                ModeCombo.SelectedIndex = 1;
                ApplyPathologyToken(initialPathologyId);
            }
            else if (!string.IsNullOrWhiteSpace(initialAcronym))
            {
                ModeCombo.SelectedIndex = 0;
                ApplyAcronymCode(initialAcronym);
            }
            else if (initialState is { } st)
            {
                ModeCombo.SelectedIndex = 0;
                var acr = TreatmentRhythmMap.AcronymsFor(st).FirstOrDefault()
                    ?? (st == ClinicalRhythmState.Asystole ? "ASYSTOLE" : null);
                if (acr is not null) ApplyAcronymCode(acr);
                else ApplyAcronymCode(st.ToString());
            }
            else
            {
                ModeCombo.SelectedIndex = isResult ? 0 : 2; // Acronym or Display only
            }

            AcronymBox.Visibility = ModeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            RealRhythmBox.Visibility = ModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

            var grid = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(ModeCombo, 0);
            grid.Children.Add(ModeCombo);

            var boxGrid = new Grid();
            boxGrid.Children.Add(AcronymBox);
            boxGrid.Children.Add(RealRhythmBox);
            Grid.SetColumn(boxGrid, 1);
            grid.Children.Add(boxGrid);

            Container = grid;
        }

        public void Commit()
        {
            if (ModeCombo.SelectedIndex == 0)
            {
                var text = AcronymBox.Text.Trim();
                var code = text.Contains(" — ") ? text.Split(" — ")[0].Trim() : text;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var entry = Taxonomy.Shared.Find(code);
                    SelectedAcronym = entry?.Acronym ?? code.ToUpperInvariant();
                    DerivedState = TreatmentRhythmMap.ClassifyByAcronyms(new[] { SelectedAcronym });
                    SelectedPathologyId = null;
                }
                else
                {
                    SelectedAcronym = null;
                    DerivedState = null;
                    SelectedPathologyId = null;
                }
            }
            else if (ModeCombo.SelectedIndex == 1)
            {
                var rawId = RealRhythmBox.Text;
                if (rawId.Contains(" — ")) rawId = rawId.Split(" — ")[0].Trim();
                if (rawId.StartsWith("№") && rawId.Contains(" ")) rawId = rawId.Substring(rawId.IndexOf(' ') + 1).Trim();
                rawId = rawId.Trim();
                if (!string.IsNullOrWhiteSpace(rawId))
                {
                    SelectedPathologyId = rawId;
                }
                else
                {
                    SelectedPathologyId = null;
                }
            }
            else
            {
                SelectedAcronym = null;
                SelectedPathologyId = null;
                DerivedState = null;
            }
        }
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
        public RhythmChoiceControl Choice = null!;
        public NumberBox Weight = null!;
        public FrameworkElement Container = null!;
        public LocText ReadText() => ReadLoc(Box, OtherEn, OtherRu, Ru);
        public bool IsEmpty { get { var t = ReadText(); return string.IsNullOrWhiteSpace(t.En) && string.IsNullOrWhiteSpace(t.Ru); } }
        public ResultItem Read()
        {
            Choice.Commit();
            return new ResultItem
            {
                Kind = (RhythmKind)Kind.SelectedIndex,
                Text = ReadText(),
                State = Choice.DerivedState,
                TargetPathologyId = Choice.SelectedPathologyId,
                TargetAcronym = Choice.SelectedAcronym,
                Weight = double.IsNaN(Weight.Value) ? 1 : Weight.Value,
            };
        }
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

        var choice = new RhythmChoiceControl(
            _appVm?.Repository.Pathologies() ?? new List<PathologyEntry>(),
            Ru,
            value?.State,
            value?.TargetPathologyId,
            value?.TargetAcronym,
            isResult: true);

        var row = new ResultRow
        {
            Kind = EnumCombo(RhythmKindLabels, value is null ? 0 : (int)value.Kind),
            Box = new TextBox { Text = LocDisplay(value?.Text, Ru) },
            OtherEn = value?.Text.En ?? string.Empty,
            OtherRu = value?.Text.Ru ?? string.Empty,
            Ru = Ru,
            Choice = choice,
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

        choice.TitleResolved += title =>
        {
            if (title is not null && string.IsNullOrWhiteSpace(row.Box.Text))
            {
                row.Box.Text = Ru ? title.Ru : title.En;
                row.OtherEn = title.En;
                row.OtherRu = title.Ru;
            }
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
        g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        var rhythmCol = LabeledColumn(AppStrings.TpFieldResultState, choice.Container);
        var weightCol = LabeledColumn(AppStrings.TpFieldWeight, row.Weight);
        Grid.SetColumn(rhythmCol, 0);
        Grid.SetColumn(weightCol, 1);
        g2.Children.Add(rhythmCol);
        g2.Children.Add(weightCol);

        stack.Children.Add(g1);
        stack.Children.Add(g2);
        box.Child = stack;
        host.Children.Add(box);
        rows.Add(row);
    }

    private void WirePathologySuggest(AutoSuggestBox box)
    {
        var ru = Ru;
        var pathologies = _appVm?.Repository.Pathologies() ?? new List<PathologyEntry>();
        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var needle = box.Text.Trim();
            if (needle.Length == 0) { box.ItemsSource = null; return; }
            var items = pathologies
                .Where(p => p.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (p.TitleEn?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (p.ResolvedNameRu?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10)
                .Select(p => $"{p.Id} — {(ru ? p.ResolvedNameRu ?? p.TitleEn : p.TitleEn)}")
                .ToList();
            items.AddRange(new[] { PathologyEntry.SyntheticAsystole, PathologyEntry.SyntheticTorsades }
                .Where(p => p.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (ru ? p.NameRu ?? p.TitleEn : p.TitleEn).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Id} — {(ru ? p.NameRu ?? p.TitleEn : p.TitleEn)}"));
            box.ItemsSource = items;
        };
        box.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string s && s.Contains(" — "))
                box.Text = s.Split(" — ")[0];
        };
    }

    // ── Row controls (edit / reorder / delete) ──────────────────────────────────
    /// <summary><paramref name="deleteOverride"/> replaces the plain row removal for lists whose deletion has
    /// side effects (custom drugs unbind the transitions that fired on them).</summary>
    private StackPanel RowControls<T>(IList list, int index, T item, System.Func<Task> editAsync, System.Action? deleteOverride = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(MoveButton("▲", () => Move(list, index, -1)));
        panel.Children.Add(MoveButton("▼", () => Move(list, index, +1)));
        panel.Children.Add(EditButton(() => editAsync()));
        panel.Children.Add(DeleteButton(deleteOverride ?? (() => Delete(list, index))));
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
            Foreground = AppTheme.AppTextPrimary,
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
            Height = 1,
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
        return HScroll(table);
    }

    // Room for an expanded (hovered / always-shown) horizontal scrollbar: ScrollBarSize is 12–16 px.
    private const double HScrollGutter = 18;

    /// <summary>Horizontal-only scroller. WinUI draws the scrollbar as an overlay on top of the content's
    /// bottom edge (e.g. it covered the ACLS flow nodes), so while the content overflows a bottom gutter is
    /// reserved for it; when everything fits no scrollbar shows and no blank strip is added.</summary>
    private static ScrollViewer HScroll(FrameworkElement content)
    {
        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = content,
        };

        void UpdateGutter()
        {
            var bottom = content.ActualWidth > viewer.ActualWidth + 0.5 ? HScrollGutter : 0;
            if (viewer.Padding.Bottom != bottom) viewer.Padding = new Thickness(0, 0, 0, bottom);
        }
        viewer.SizeChanged += (_, _) => UpdateGutter();
        content.SizeChanged += (_, _) => UpdateGutter();
        return viewer;
    }

    private static Grid BuildTable(IReadOnlyList<string> headers, List<UIElement[]> rows, IReadOnlyList<double> starWidths, double minWidth)
    {
        var grid = new Grid { MinWidth = minWidth };
        for (int c = 0; c < headers.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(starWidths[c], GridUnitType.Star) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Count; c++)
            PlaceCell(grid, TableCell(HeaderText(headers[c]), header: true), 0, c);

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < headers.Count && c < rows[r].Length; c++)
                PlaceCell(grid, TableCell(rows[r][c], header: false), r + 1, c);
        }
        return grid;
    }

    private static TextBlock HeaderText(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = AppTheme.AppTextPrimary,
        TextWrapping = TextWrapping.Wrap,
    };

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
}
