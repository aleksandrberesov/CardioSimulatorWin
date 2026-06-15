using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
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
    }

    public void Initialize(OskeViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
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
        // Start area: centered intro + "Start" button.
        var startStack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        startStack.Children.Add(new TextBlock
        {
            Text = AppStrings.OskeIntro,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
        });
        var startBtn = new Button { Content = AppStrings.OskeStart, HorizontalAlignment = HorizontalAlignment.Center };
        startBtn.Click += async (_, _) => await OnStartAsync();
        startStack.Children.Add(startBtn);
        _startArea = startStack;

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
        var results = _appVm.OskeResultStore.List();
        if (results.Count == 0) return Placeholder(AppStrings.OskeResultsEmpty);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8) };
        foreach (var r in results)
            list.Items.Add(new ListViewItem { Content = BuildResultListItem(r), Tag = r });
        Grid.SetColumn(list, 0);
        grid.Children.Add(list);

        var detail = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8),
        };
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem item && item.Tag is OskeResult r)
                detail.Content = BuildResultDetail(r);
        };
        list.SelectedIndex = 0;
        return grid;
    }

    private UIElement BuildResultListItem(OskeResult r)
    {
        var panel = new StackPanel { Spacing = 2 };
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

    private async Task OnStartAsync()
    {
        if (_vm is null || _appVm is null || _rhythmVm is null || _monitorVm is null) return;
        var picked = await ShowStartDialogAsync();
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

    private async Task<StartChoice?> ShowStartDialogAsync()
    {
        var fio = new TextBox { Header = AppStrings.OskeFieldFullName };
        var group = new TextBox { Header = AppStrings.OskeFieldGroup };

        var specialtyBox = new ComboBox { Header = AppStrings.OskeFieldSpecialty, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (sp, label) in SpecialtyOptions())
            specialtyBox.Items.Add(new ComboBoxItem { Content = label, Tag = sp });
        specialtyBox.SelectedIndex = 0;

        var ecgBox = new ComboBox { Header = AppStrings.OskeFieldEcg, HorizontalAlignment = HorizontalAlignment.Stretch };
        var hint = new TextBlock
        {
            Text = AppStrings.OskeNoEcgs,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.Tomato),
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(fio);
        panel.Children.Add(group);
        panel.Children.Add(specialtyBox);
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
        };

        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) &&
            !string.IsNullOrWhiteSpace(group.Text) &&
            ecgBox.SelectedItem is ComboBoxItem;

        void RepopulateEcg()
        {
            ecgBox.Items.Clear();
            var sp = (OskeSpecialty)((ComboBoxItem)specialtyBox.SelectedItem).Tag;
            foreach (var id in _vm!.AvailableEcgIds(sp))
                ecgBox.Items.Add(new ComboBoxItem { Content = EcgLabel(id), Tag = id });
            var none = ecgBox.Items.Count == 0;
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
        if (ecgBox.SelectedItem is not ComboBoxItem ecgItem) return null;
        var specialty = (OskeSpecialty)((ComboBoxItem)specialtyBox.SelectedItem).Tag;
        return new StartChoice(
            new OskeStudentInfo(fio.Text.Trim(), group.Text.Trim()),
            specialty,
            (string)ecgItem.Tag);
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
        return _appVm!.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;
    }
}
