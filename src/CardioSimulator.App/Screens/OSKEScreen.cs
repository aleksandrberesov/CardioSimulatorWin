using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// OSCE (ОСКЭ) station screen. A sub-tab bar (Экзамен / Результаты) hosts the exam flow: a start
/// dialog collects ФИО + группа + specialty + ECG, then the chosen 12-lead trace shows on the left
/// (zoomable <see cref="MonitorView"/>) beside the scrollable conclusion form on the right. Finishing
/// grades the answers against the ECG's key (<see cref="OskeGrader"/>), saves the result, and shows
/// the per-block ✓/✗ breakdown. The answer-key/form constructor is a separate top-level mode.
/// </summary>
/// <remarks>
/// The exam/start/results areas are built once and toggled via <see cref="UIElement.Visibility"/>
/// rather than swapped in/out of the tree: the Win2D-backed <see cref="EcgMonitorControl"/> tears
/// itself down on <c>Unloaded</c> (releasing its swap chain), so re-parenting it would destroy it and
/// crash the XAML layer on the next layout. Keeping it permanently parented avoids that.
/// </remarks>
public sealed class OSKEScreen : UserControl
{
    private OskeViewModel? _vm;
    public OskeViewModel? ViewModel => _vm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;

    private readonly MonitorView _monitor = new();
    private readonly Grid _root = new();
    private Button _examTab = null!;
    private Button _resultsTab = null!;
    private string _tab = "exam";

    // Persistent content areas (toggled by Visibility, never removed from the tree).
    private readonly Grid _contentArea = new();
    private FrameworkElement _startArea = null!;
    private readonly Grid _startHost = new();          // holds the (re-rendered) start card
    private OskeSpecialty _startSpecialty = OskeSpecialty.Therapy;
    private Grid _examArea = null!;
    private readonly ContentControl _resultsArea = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly ContentControl _examBanner = new();
    private readonly ScrollViewer _examScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _examFooter = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 8, 0, 0),
    };

    public OSKEScreen()
    {
        Content = BuildShell();
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => AppTheme.Changed -= OnThemeChanged;
    }

    // Themed brushes are cached instances swapped on theme change, so the start card must be rebuilt
    // to re-pull them (mirrors QuickTestScreen). Only the start card holds persistent themed content;
    // the exam/results areas are rebuilt on demand.
    private void OnThemeChanged() => RenderStartArea();

    public void Initialize(OskeViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
        RenderStartArea(); // now that the VM is bound, reflect real available-ECG counts per specialty
        ShowTab("exam");
    }

    // ── Shell + tabs ───────────────────────────────────────────────────────

    private UIElement BuildShell()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(12, 8, 12, 8) };
        _examTab = TabButton(AppStrings.OskeTabExam, () => ShowTab("exam"));
        _resultsTab = TabButton(AppStrings.OskeTabResults, () => ShowTab("results"));
        tabBar.Children.Add(_examTab);
        tabBar.Children.Add(_resultsTab);
        Grid.SetRow(tabBar, 0);
        _root.Children.Add(tabBar);

        BuildContentArea();
        Grid.SetRow(_contentArea, 1);
        _root.Children.Add(_contentArea);
        return _root;
    }

    private void BuildContentArea()
    {
        // Start area: a themed intro card (header + specialty picker + steps + Start) that mirrors the
        // Testing / Examination screens. Its content is (re)built by RenderStartArea so it re-pulls the
        // cached theme brushes on theme change and reflects the current available-ECG counts.
        _startArea = _startHost;
        RenderStartArea();

        // Exam area: persistent 2-pane layout — the monitor lives here for the screen's lifetime.
        _examArea = new Grid { Visibility = Visibility.Collapsed };
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(_monitor, 0);
        _examArea.Children.Add(_monitor);

        var right = new Grid { Padding = new Thickness(12) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_examBanner, 0);
        right.Children.Add(_examBanner);
        Grid.SetRow(_examScroll, 1);
        right.Children.Add(_examScroll);
        Grid.SetRow(_examFooter, 2);
        right.Children.Add(_examFooter);
        Grid.SetColumn(right, 1);
        _examArea.Children.Add(right);

        _resultsArea.Visibility = Visibility.Collapsed;

        _contentArea.Children.Add(_startArea);
        _contentArea.Children.Add(_examArea);
        _contentArea.Children.Add(_resultsArea);
    }

    private static Button TabButton(string text, Action onClick)
    {
        var btn = new Button { Content = text, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ── Start card (themed intro, matches Testing / Examination) ─────────────

    /// <summary>Rebuilds the start card into its persistent host. Called on first build, on theme change,
    /// and after Reset so the specialty counts / selection stay current.</summary>
    private void RenderStartArea()
    {
        EnsureValidStartSpecialty();
        _startHost.Children.Clear();
        _startHost.Children.Add(BuildStartCard());
    }

    /// <summary>Keeps <see cref="_startSpecialty"/> on a specialty that actually has authored ECGs when
    /// one exists, so the default selection is startable (a specialty with none can't begin an attempt).</summary>
    private void EnsureValidStartSpecialty()
    {
        if (_vm is null) return;
        if (_vm.AvailableEcgIds(_startSpecialty).Count > 0) return;
        foreach (var (sp, _) in SpecialtyOptions())
        {
            if (_vm.AvailableEcgIds(sp).Count > 0) { _startSpecialty = sp; return; }
        }
    }

    private UIElement BuildStartCard()
    {
        var content = new StackPanel { Spacing = 18 };
        content.Children.Add(BuildStartHeader());
        content.Children.Add(Hairline());
        content.Children.Add(new TextBlock
        {
            Text = AppStrings.OskeSpecialtyChoose,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
        });
        content.Children.Add(BuildSpecialtyCards());
        content.Children.Add(BuildHowItWorks());
        content.Children.Add(Hairline());
        content.Children.Add(BuildStartButton());

        var card = new Border
        {
            Child = content,
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(28, 24, 28, 24),
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
        return card;
    }

    private UIElement BuildStartHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };

        var avatar = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = AppTheme.AppAccentSoftBackground,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\U0001FAC0", // 🫀 anatomical heart
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(avatar);

        var titles = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = AppStrings.OskeStartTitle,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = AppTheme.TextPrimary,
        });
        titles.Children.Add(new TextBlock
        {
            Text = AppStrings.OskeIntro,
            FontSize = 13,
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        });
        row.Children.Add(titles);
        return row;
    }

    private UIElement BuildSpecialtyCards()
    {
        var defs = new (OskeSpecialty Specialty, string Icon, string Label)[]
        {
            (OskeSpecialty.Therapy, "\U0001FA7A", AppStrings.OskeSpecialtyTherapy),            // 🩺 stethoscope
            (OskeSpecialty.Cardiology, "\U0001FAC0", AppStrings.OskeSpecialtyCardiology),      // 🫀 heart
            (OskeSpecialty.FunctionalDiagnostics, "\U0001F4C8", AppStrings.OskeSpecialtyFd),   // 📈 chart
        };

        var grid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < defs.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < defs.Length; i++)
        {
            var card = BuildSpecialtyCard(defs[i].Specialty, defs[i].Icon, defs[i].Label);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        return grid;
    }

    private Button BuildSpecialtyCard(OskeSpecialty specialty, string icon, string label)
    {
        var count = _vm?.AvailableEcgIds(specialty).Count ?? 0;
        var enabled = count > 0;
        var selected = enabled && specialty == _startSpecialty;

        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(new TextBlock { Text = icon, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = enabled ? AppStrings.OskeSpecialtyEcgCount(count) : AppStrings.OskeSpecialtyNoEcg,
            FontSize = 11,
            Foreground = enabled ? AppTheme.Accent : AppTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        var btn = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = selected ? AppTheme.AppAccentSoftBackground : AppTheme.AppSubtleFill,
            BorderBrush = selected ? AppTheme.Accent : AppTheme.AppCardBorder,
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 16, 12, 16),
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.55,
        };
        btn.Click += (_, _) => { _startSpecialty = specialty; RenderStartArea(); };
        return btn;
    }

    private static UIElement BuildHowItWorks()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.OskeHowTitle,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
        });
        var steps = new[] { AppStrings.OskeHowStep1, AppStrings.OskeHowStep2, AppStrings.OskeHowStep3 };
        for (var i = 0; i < steps.Length; i++)
            stack.Children.Add(BuildStepRow(i + 1, steps[i]));
        return stack;
    }

    private static UIElement BuildStepRow(int number, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = AppTheme.AppAccentSoftBackground,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = AppTheme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 560,
        });
        return row;
    }

    private Button BuildStartButton()
    {
        var btn = new Button
        {
            Content = AppStrings.OskeStart,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
            btn.Style = s;
        btn.Click += async (_, _) => await OnStartAsync(_startSpecialty);
        return btn;
    }

    private static Border Hairline() => new()
    {
        Height = 1,
        Background = AppTheme.AppCardBorder,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private void ShowTab(string tab)
    {
        _tab = tab;
        UpdateTabButtons();
        if (tab == "results")
        {
            _resultsArea.Content = BuildResultsContent();
            _startArea.Visibility = Visibility.Collapsed;
            _examArea.Visibility = Visibility.Collapsed;
            _resultsArea.Visibility = Visibility.Visible;
            _monitorVm?.SetIsRunning(false);
        }
        else
        {
            UpdateExamView();
        }
    }

    private void UpdateTabButtons()
    {
        _examTab.FontWeight = _tab == "exam" ? FontWeights.Bold : FontWeights.Normal;
        _resultsTab.FontWeight = _tab == "results" ? FontWeights.Bold : FontWeights.Normal;
    }

    private static UIElement Placeholder(string text) => new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Colors.Gray),
    };

    // ── Exam view (start / taking / graded) ─────────────────────────────────

    /// <summary>Reflects the current attempt state into the persistent areas (no re-parenting).</summary>
    private void UpdateExamView()
    {
        _resultsArea.Visibility = Visibility.Collapsed;

        // Start state: no attempt in progress and nothing graded.
        if (_vm is null || (_vm.Result is null && !_vm.IsTakingExam))
        {
            _startArea.Visibility = Visibility.Visible;
            _examArea.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
            return;
        }

        var graded = _vm.Result is not null;
        _startArea.Visibility = Visibility.Collapsed;
        _examArea.Visibility = Visibility.Visible;

        if (_vm.EcgId is not null) _rhythmVm?.SelectRhythm(_vm.EcgId, persist: false);
        _monitorVm?.SetIsRunning(!graded);

        _examBanner.Content = graded && _vm.Result is { } res ? BuildResultBanner(res) : null;
        _examScroll.Content = BuildQuestionnaire(graded);

        _examFooter.Children.Clear();
        if (graded)
        {
            var newBtn = new Button { Content = AppStrings.OskeNewAttempt };
            newBtn.Click += (_, _) => OnNewAttempt();
            _examFooter.Children.Add(newBtn);
        }
        else
        {
            var finish = new Button { Content = AppStrings.OskeFinish };
            finish.Click += async (_, _) => await OnFinishAsync();
            _examFooter.Children.Add(finish);
        }
    }

    private UIElement BuildQuestionnaire(bool graded)
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(4, 4, 12, 4) };
        var form = _vm!.Form!;
        var blockResults = graded && _vm.Result is { } r
            ? r.Blocks.ToDictionary(b => b.QuestionId)
            : new Dictionary<string, OskeBlockResult>();

        foreach (var q in form.Questions)
        {
            if (graded)
            {
                blockResults.TryGetValue(q.Id, out var br);
                panel.Children.Add(BuildGradedBlock(
                    br ?? new OskeBlockResult(q.Id, Array.Empty<string>(), Array.Empty<string>(), false), q));
                continue;
            }

            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock
            {
                Text = $"{q.Number}. {q.Title}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            foreach (var opt in q.Options)
            {
                var qid = q.Id;
                var oid = opt.Id;
                if (q.Kind == OskeAnswerKind.Single)
                {
                    var rb = new RadioButton
                    {
                        Content = WrapText(opt.Text),
                        GroupName = "oske_" + qid,
                        IsChecked = _vm.IsSelected(qid, oid),
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    rb.Checked += (_, _) => _vm.SetSingle(qid, oid);
                    block.Children.Add(rb);
                }
                else
                {
                    var cb = new CheckBox
                    {
                        Content = WrapText(opt.Text),
                        IsChecked = _vm.IsSelected(qid, oid),
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    cb.Checked += (_, _) => _vm.ToggleMulti(qid, oid, true);
                    cb.Unchecked += (_, _) => _vm.ToggleMulti(qid, oid, false);
                    block.Children.Add(cb);
                }
            }

            panel.Children.Add(block);
        }
        return panel;
    }

    /// <summary>
    /// Renders one graded block (header ✓/✗ + the key and the student's picks, colored). Shared by the
    /// post-submit exam view and the saved-results detail. <paramref name="q"/> supplies option text;
    /// when it's null (form changed since the attempt) ids are shown verbatim.
    /// </summary>
    private static UIElement BuildGradedBlock(OskeBlockResult b, OskeQuestion? q)
    {
        var block = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = b.IsCorrect ? "✓" : "✗",
            Foreground = new SolidColorBrush(b.IsCorrect ? Colors.LimeGreen : Colors.Tomato),
            FontWeight = FontWeights.Bold,
        });
        header.Children.Add(new TextBlock
        {
            Text = q is null ? b.QuestionId : $"{q.Number}. {q.Title}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        block.Children.Add(header);

        var ids = q is null
            ? b.Correct.Concat(b.Selected).Distinct()
            : q.Options.Select(o => o.Id).Where(id => b.Correct.Contains(id) || b.Selected.Contains(id));

        foreach (var id in ids)
        {
            var isCorrect = b.Correct.Contains(id);
            var color = isCorrect ? Colors.LimeGreen : Colors.Tomato;
            var text = q?.Options.FirstOrDefault(o => o.Id == id)?.Text ?? id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = isCorrect ? "✓" : "✗", Foreground = new SolidColorBrush(color), Width = 14 });
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(color) });
            block.Children.Add(row);
        }

        if (b.Selected.Count == 0)
        {
            block.Children.Add(new TextBlock
            {
                Text = AppStrings.OskeUnanswered,
                Margin = new Thickness(16, 0, 0, 0),
                Opacity = 0.7,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
            });
        }
        return block;
    }

    private static TextBlock WrapText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private FrameworkElement BuildResultBanner(OskeResult res)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(res.Passed ? Color.FromArgb(40, 0, 200, 0) : Color.FromArgb(40, 220, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = res.Passed ? AppStrings.OskePassed : AppStrings.OskeFailed,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(res.Passed ? Colors.LimeGreen : Colors.Tomato),
        });
        stack.Children.Add(new TextBlock { Text = AppStrings.OskeScoreFormat(res.CorrectCount, res.TotalCount) });
        stack.Children.Add(new TextBlock
        {
            Text = $"{res.Student.FullName} · {res.Student.Group}",
            Opacity = 0.7,
            FontSize = 12,
        });
        border.Child = stack;
        return border;
    }

    // ── Results tab ────────────────────────────────────────────────────────

    private UIElement BuildResultsContent()
    {
        if (_appVm is null) return new Grid();
        var entries = _appVm.OskeResultStore.ListEntries();
        if (entries.Count == 0) return Placeholder(AppStrings.OskeResultsEmpty);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        // Left column: the results list plus a delete / edit / clear-all toolbar beneath it. The list acts
        // on the selection; the whole column rebuilds from disk after any mutation so it stays in sync.
        var leftColumn = new Grid();
        leftColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8) };
        foreach (var e in entries)
            list.Items.Add(new ListViewItem { Content = BuildResultListItem(e.Result), Tag = e });
        Grid.SetRow(list, 0);
        leftColumn.Children.Add(list);

        var editBtn = new Button { Content = AppStrings.CommonEdit, IsEnabled = false };
        var deleteBtn = new Button { Content = AppStrings.CommonDelete, IsEnabled = false };
        var clearBtn = new Button { Content = AppStrings.ResultsClearAll };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(8, 4, 8, 8),
        };
        toolbar.Children.Add(editBtn);
        toolbar.Children.Add(deleteBtn);
        toolbar.Children.Add(clearBtn);
        Grid.SetRow(toolbar, 1);
        leftColumn.Children.Add(toolbar);

        Grid.SetColumn(leftColumn, 0);
        grid.Children.Add(leftColumn);

        var detail = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8),
        };
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);

        OskeResultStore.Entry? Selected() => (list.SelectedItem as ListViewItem)?.Tag as OskeResultStore.Entry;

        list.SelectionChanged += (_, _) =>
        {
            var entry = Selected();
            editBtn.IsEnabled = entry is not null;
            deleteBtn.IsEnabled = entry is not null;
            detail.Content = entry is null ? null : BuildResultDetail(entry.Result);
        };

        editBtn.Click += async (_, _) => { if (Selected() is { } e) await EditResultAsync(e); };
        deleteBtn.Click += async (_, _) => { if (Selected() is { } e) await DeleteResultAsync(e); };
        clearBtn.Click += async (_, _) => await ClearResultsAsync(entries.Count);

        list.SelectedIndex = 0;
        return grid;
    }

    private async Task DeleteResultAsync(OskeResultStore.Entry entry)
    {
        if (_appVm is null) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.ResultsDeleteTitle,
            Content = AppStrings.ResultsDeleteConfirm,
            PrimaryButtonText = AppStrings.CommonDelete,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _appVm.OskeResultStore.Delete(entry.Path);
        _resultsArea.Content = BuildResultsContent();
    }

    private async Task ClearResultsAsync(int count)
    {
        if (_appVm is null) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.ResultsClearTitle,
            Content = AppStrings.ResultsClearConfirm(count),
            PrimaryButtonText = AppStrings.ResultsClearAll,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _appVm.OskeResultStore.Clear();
        _resultsArea.Content = BuildResultsContent();
    }

    // Edits an attempt's student identity and grade in place. The per-block ✓/✗ breakdown is left
    // untouched — this is a manual override / appeal, not a re-grade — so the summary may legitimately
    // diverge from the per-block tally after a correction.
    private async Task EditResultAsync(OskeResultStore.Entry entry)
    {
        if (_appVm is null) return;
        var r = entry.Result;

        var fio = new TextBox { Header = AppStrings.ResultsEditFullName, Text = r.Student.FullName };
        var group = new TextBox { Header = AppStrings.ResultsEditGroup, Text = r.Student.Group };
        var correct = new NumberBox
        {
            Header = $"{AppStrings.ResultsEditCorrect} (0–{r.TotalCount})",
            Value = r.CorrectCount,
            Minimum = 0,
            Maximum = r.TotalCount,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        var passed = new ToggleSwitch { Header = AppStrings.ResultsEditPassed, IsOn = r.Passed };

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(fio);
        panel.Children.Add(group);
        panel.Children.Add(correct);
        panel.Children.Add(passed);

        var dialog = new ContentDialog
        {
            Title = AppStrings.ResultsEditTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CommonSave,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) && !string.IsNullOrWhiteSpace(group.Text);
        fio.TextChanged += (_, _) => Revalidate();
        group.TextChanged += (_, _) => Revalidate();
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newCorrect = double.IsNaN(correct.Value)
            ? r.CorrectCount
            : (int)Math.Clamp(correct.Value, 0, r.TotalCount);
        var edited = r with
        {
            Student = r.Student with { FullName = fio.Text.Trim(), Group = group.Text.Trim() },
            CorrectCount = newCorrect,
            Passed = passed.IsOn,
        };
        var ok = _appVm.OskeResultStore.Overwrite(entry.Path, edited);
        _resultsArea.Content = BuildResultsContent();
        if (!ok) await ShowResultsErrorAsync();
    }

    private async Task ShowResultsErrorAsync()
    {
        var dialog = new ContentDialog
        {
            Title = AppStrings.ResultsEditTitle,
            Content = AppStrings.ResultsSaveFailed,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        await dialog.ShowAsync();
    }

    private UIElement BuildResultListItem(OskeResult r)
    {
        // Roomier line spacing + an inset margin so the three lines aren't jammed together or against the
        // row edges. Margin (not an opaque card background) keeps the ListView's selection highlight,
        // which fills the row behind the inset content, visible. Mirrors the Examination results card.
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(4, 8, 4, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = r.Student.FullName,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{r.Student.Group} · {SpecialtyLabel(r.Specialty)} · {r.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}",
            Opacity = 0.8,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{(r.Passed ? AppStrings.OskePassed : AppStrings.OskeFailed)} — {AppStrings.OskeScoreFormat(r.CorrectCount, r.TotalCount)}",
            Foreground = new SolidColorBrush(r.Passed ? Colors.LimeGreen : Colors.Tomato),
            FontSize = 12,
        });
        return panel;
    }

    private UIElement BuildResultDetail(OskeResult r)
    {
        var form = _appVm!.OskeRepository.Form(r.FormId) ?? _appVm.OskeRepository.FormFor(r.Specialty);
        var lookup = form.Questions.ToDictionary(q => q.Id);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(4) };
        panel.Children.Add(BuildResultBanner(r));
        panel.Children.Add(new TextBlock
        {
            Text = $"{SpecialtyLabel(r.Specialty)} · {EcgLabel(r.EcgId)} · {r.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}",
            Opacity = 0.8,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var b in r.Blocks)
        {
            lookup.TryGetValue(b.QuestionId, out var q);
            panel.Children.Add(BuildGradedBlock(b, q));
        }

        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private static string SpecialtyLabel(OskeSpecialty specialty) => specialty switch
    {
        OskeSpecialty.Therapy => AppStrings.OskeSpecialtyTherapy,
        OskeSpecialty.Cardiology => AppStrings.OskeSpecialtyCardiology,
        _ => AppStrings.OskeSpecialtyFd,
    };

    // ── Flow handlers ──────────────────────────────────────────────────────

    private async Task OnStartAsync(OskeSpecialty? preselect = null)
    {
        if (_vm is null || _appVm is null || _rhythmVm is null || _monitorVm is null) return;
        var picked = await ShowStartDialogAsync(preselect);
        if (picked is null) return;
        _vm.StartAttempt(picked.Student, picked.Specialty, picked.EcgId);
        UpdateExamView();
    }

    private async Task OnFinishAsync()
    {
        if (_vm is null) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.OskeFinishConfirmTitle,
            Content = AppStrings.OskeFinishConfirm,
            PrimaryButtonText = AppStrings.OskeFinish,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.Submit();
        UpdateExamView();
    }

    private void OnNewAttempt()
    {
        _vm?.Reset();
        UpdateExamView();
    }

    private sealed record StartChoice(OskeStudentInfo Student, OskeSpecialty Specialty, string EcgId);

    private async Task<StartChoice?> ShowStartDialogAsync(OskeSpecialty? preselect = null)
    {
        var fio = new TextBox { Header = AppStrings.OskeFieldFullName };
        var group = new TextBox { Header = AppStrings.OskeFieldGroup };

        var specialtyBox = new ComboBox { Header = AppStrings.OskeFieldSpecialty, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (sp, label) in SpecialtyOptions())
            specialtyBox.Items.Add(new ComboBoxItem { Content = label, Tag = sp });
        // Honour the specialty the caller picked from the start card; fall back to the first option.
        specialtyBox.SelectedIndex = preselect is { } pre
            ? Math.Max(0, SpecialtyOptions().ToList().FindIndex(o => o.Item1 == pre))
            : 0;

        var ecgHeader = new TextBlock { Text = AppStrings.OskeFieldEcg };
        var ecgBox = new RhythmPickerButton
        {
            PlaceholderText = AppStrings.OskeFieldEcg,
            ShowClearButton = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayLanguage = _appVm!.SelectedLanguage,
        };
        var hint = new TextBlock
        {
            Text = AppStrings.OskeNoEcgs,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.Tomato),
            Visibility = Visibility.Collapsed,
        };

        // Registered-student pick-list (Full-edition roster) — same affordance as the Examination flow.
        // Choosing an entry pre-fills ФИО + группа; the fields stay editable so manual entry still works.
        // Only shown when the instructor has registered students (Students screen / StudentStore).
        var roster = _appVm!.StudentStore.List();
        ComboBox? studentBox = null;
        if (roster.Count > 0)
        {
            studentBox = new ComboBox { Header = AppStrings.ExamPickStudent, HorizontalAlignment = HorizontalAlignment.Stretch };
            studentBox.Items.Add(new ComboBoxItem { Content = AppStrings.ExamPickStudentManual, Tag = null });
            foreach (var s in roster)
                studentBox.Items.Add(new ComboBoxItem { Content = $"{s.FullName} · {s.Group}", Tag = s });
            studentBox.SelectedIndex = 0;
            studentBox.SelectionChanged += (_, _) =>
            {
                if ((studentBox.SelectedItem as ComboBoxItem)?.Tag is Student picked)
                {
                    fio.Text = picked.FullName;
                    group.Text = picked.Group;
                }
            };
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        if (studentBox is not null) panel.Children.Add(studentBox);
        panel.Children.Add(fio);
        panel.Children.Add(group);
        panel.Children.Add(specialtyBox);
        panel.Children.Add(ecgHeader);
        panel.Children.Add(ecgBox);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = AppStrings.OskeStartTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.OskeStart,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = Theming.AppTheme.Current,
        };

        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) &&
            !string.IsNullOrWhiteSpace(group.Text) &&
            ecgBox.SelectedId is not null;

        void RepopulateEcg()
        {
            var sp = (OskeSpecialty)((ComboBoxItem)specialtyBox.SelectedItem).Tag;
            var available = _vm!.AvailableEcgIds(sp).ToHashSet();
            var entries = _rhythmVm?.Rhythms.Where(r => available.Contains(r.Id)).ToList() ?? new List<PathologyEntry>();
            // Drop a selection that the new specialty doesn't offer.
            if (ecgBox.SelectedId is { } sel && !available.Contains(sel)) ecgBox.SelectedId = null;
            ecgBox.DisplayLanguage = _appVm!.SelectedLanguage;
            ecgBox.SetRhythms(entries);
            var none = entries.Count == 0;
            hint.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            ecgBox.IsEnabled = !none;
            Revalidate();
        }

        specialtyBox.SelectionChanged += (_, _) => RepopulateEcg();
        ecgBox.SelectionChanged += (_, _) => Revalidate();
        fio.TextChanged += (_, _) => Revalidate();
        group.TextChanged += (_, _) => Revalidate();
        RepopulateEcg();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if (ecgBox.SelectedId is not { } ecgId) return null;
        var specialty = (OskeSpecialty)((ComboBoxItem)specialtyBox.SelectedItem).Tag;
        return new StartChoice(
            new OskeStudentInfo(fio.Text.Trim(), group.Text.Trim()),
            specialty,
            ecgId);
    }

    private static IEnumerable<(OskeSpecialty, string)> SpecialtyOptions() => new[]
    {
        (OskeSpecialty.Therapy, AppStrings.OskeSpecialtyTherapy),
        (OskeSpecialty.Cardiology, AppStrings.OskeSpecialtyCardiology),
        (OskeSpecialty.FunctionalDiagnostics, AppStrings.OskeSpecialtyFd),
    };

    private string EcgLabel(string id)
    {
        var entry = _rhythmVm?.Rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return id;
        return _appVm!.SelectedLanguage == DomainLanguage.RU ? (entry.ResolvedNameRu ?? entry.TitleEn) : entry.TitleEn;
    }
}
