using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Network;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Examination («Экзамен») screen — the «Формирование теста» flow. The start area offers
/// <b>Индивидуальное</b> (register → a 10/20/30-question test is generated from the bank → taken on this
/// PC → graded + saved) and <b>Групповое</b> (a QR to the LAN <see cref="GroupTestServer"/> is shown;
/// students register on their phones, each gets an individually-generated test, and results land in the
/// same report). A sub-tab bar (Экзамен / Результаты) hosts the flow and the saved-results list, mirroring
/// the OSCE storage + grading pipeline (<see cref="ExamResultStore"/> / <see cref="ExamGrader"/>).
/// </summary>
/// <remarks>
/// Like <see cref="OSKEScreen"/>, the areas are built once and toggled via <see cref="UIElement.Visibility"/>
/// rather than swapped in/out of the tree: the Win2D <c>EcgMonitorControl</c> tears itself down on
/// <c>Unloaded</c>, so re-parenting it would crash the XAML layer.
/// </remarks>
public sealed class ExaminationScreen : UserControl
{
    private ExaminationViewModel? _vm;
    public ExaminationViewModel? ViewModel => _vm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private string? _loadedQuestionId;

    /// <summary>Raised when the ECG monitor's visibility changes (e.g. toggled for ECG vs image stimulus).</summary>
    public event EventHandler<bool>? MonitorVisibilityChanged;

    private readonly MonitorView _monitor = new();
    private readonly Image _stimulusImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    // B7 (customer 28-08): the monitor's own start/stop (freeze the running ECG line) + 1–2 column
    // (lead-layout) controls, surfaced on the exam monitor. Shown only for ECG-stimulus questions.
    private Border? _examMonitorControls;
    private Action? _syncExamControls;
    // A3: when this exam was launched from the Learning Scale «Сдать», the roster student to return to the
    // dashboard with once it's graded (null = not launched from the dashboard).
    private Student? _returnToLearningScale;
    private readonly ExamQuestionPanel _questionPanel = new();
    private readonly Grid _root = new();
    private Button _examTab = null!;
    private Button _resultsTab = null!;
    private Button? _backToLectureBtn;
    private string _tab = "exam";

    private readonly Grid _contentArea = new();
    private FrameworkElement _startArea = null!;
    private QuickTestScreen _individualLauncher = null!;
    private Grid _examArea = null!;
    private readonly ScrollViewer _breakdownScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _resultsArea = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    // Group session UI.
    private bool _groupMode;
    private Grid _groupArea = null!;
    private QuickTestScreen _groupLauncher = null!;
    private StackPanel _groupLive = null!;
    private readonly Image _groupQr = new() { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center };
    private TextBlock _groupUrl = null!;
    private TextBlock _groupRosterCount = null!;
    private StackPanel _rosterHost = null!;

    public ExaminationScreen()
    {
        Content = BuildShell();
    }

    public void Initialize(ExaminationViewModel vm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _vm = vm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;
        _questionPanel.Bind(vm);

        if (appVm.SelectedCourseId is not null && _backToLectureBtn is not null)
        {
            _backToLectureBtn.Visibility = Visibility.Visible;
        }

        _individualLauncher.TestStartRequested += OnIndividualTestChosen;
        _individualLauncher.BackToLectureRequested += OnIndividualLauncherBack;

        // The Group session reuses the same customization card as Individual (ready test / generate).
        _groupLauncher.GroupSessionRequested += OnGroupConfigured;
        _groupLauncher.BackToLectureRequested += OnGroupLauncherBack;

        vm.StateChanged += OnVmStateChanged;
        appVm.GroupTestServer.ParticipantsChanged += OnParticipantsChanged;
        Unloaded += (_, _) =>
        {
            vm.StateChanged -= OnVmStateChanged;
            appVm.GroupTestServer.ParticipantsChanged -= OnParticipantsChanged;
            _individualLauncher.TestStartRequested -= OnIndividualTestChosen;
            _individualLauncher.BackToLectureRequested -= OnIndividualLauncherBack;
            _groupLauncher.GroupSessionRequested -= OnGroupConfigured;
            _groupLauncher.BackToLectureRequested -= OnGroupLauncherBack;
        };

        // Re-attach to a session that is already running (e.g. after switching modes and back).
        if (appVm.GroupTestServer.IsRunning)
        {
            _groupMode = true;
            if (appVm.GroupTestServer.Url is { } url) _ = SetQrAsync(url);
        }

        ShowTab("exam");

        // A3 (customer 28-08): a graded launch queued from the Learning Scale «Сдать» — start it directly
        // (skipping the student dialog, since the dashboard already knows the student) and remember to offer
        // a return to the dashboard once it is graded.
        if (appVm.PendingExamLaunch is { } launch)
        {
            appVm.PendingExamLaunch = null;
            if (appVm.TestRepository.Test(launch.TestId) is { Questions.Count: > 0 } test)
            {
                _returnToLearningScale = launch.ReturnStudent;
                _individualLauncher.Visibility = Visibility.Collapsed;
                vm.Start(test, launch.Student);
                UpdateExamView();
            }
        }
    }

    private void OnVmStateChanged()
    {
        if (_tab == "exam") UpdateExamView();
    }

    private void OnParticipantsChanged() => DispatcherQueue.TryEnqueue(RefreshRoster);

    // ── Shell + tabs ───────────────────────────────────────────────────────

    private UIElement BuildShell()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tabBar = new Grid { Padding = new Thickness(12, 8, 12, 8) };
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Left: tabs
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Spacer
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Right: back button

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _examTab = TabButton(AppStrings.ExamTabExam, () => ShowTab("exam"));
        _resultsTab = TabButton(AppStrings.ExamTabResults, () => ShowTab("results"));
        tabs.Children.Add(_examTab);
        tabs.Children.Add(_resultsTab);
        Grid.SetColumn(tabs, 0);
        tabBar.Children.Add(tabs);

        _backToLectureBtn = new Button
        {
            Content = AppStrings.BackToLecture,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _backToLectureBtn.Click += async (_, _) => await ReturnToLectureAsync();
        Grid.SetColumn(_backToLectureBtn, 2);
        tabBar.Children.Add(_backToLectureBtn);

        Grid.SetRow(tabBar, 0);
        _root.Children.Add(tabBar);

        BuildContentArea();
        Grid.SetRow(_contentArea, 1);
        _root.Children.Add(_contentArea);
        return _root;
    }

    private void BuildContentArea()
    {
        _startArea = BuildStartArea();

        // Individual test picker: the shared Quick Test card, course-wide (ready test / generate over
        // all course themes). Built once; initialized + shown when «Индивидуальное» is chosen.
        _individualLauncher = new QuickTestScreen { Visibility = Visibility.Collapsed };

        // Exam area: monitor (left) + question panel / graded breakdown (right). Built once.
        _examArea = new Grid { Visibility = Visibility.Collapsed };
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _examArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        // Left column: monitor and image stimulus share the slot (one visible at a time); the monitor
        // is never re-parented (its Win2D canvas tears down on Unloaded), only toggled by Visibility.
        var left = new Grid();
        left.Children.Add(_monitor);
        _stimulusImage.Visibility = Visibility.Collapsed;
        left.Children.Add(_stimulusImage);
        _examMonitorControls = BuildExamMonitorControls();
        _examMonitorControls.Visibility = Visibility.Collapsed;
        left.Children.Add(_examMonitorControls);
        Grid.SetColumn(left, 0);
        _examArea.Children.Add(left);

        var right = new Grid();
        Grid.SetColumn(right, 1);
        _breakdownScroll.Visibility = Visibility.Collapsed;
        right.Children.Add(_questionPanel);
        right.Children.Add(_breakdownScroll);
        _examArea.Children.Add(right);

        _resultsArea.Visibility = Visibility.Collapsed;

        _groupArea = BuildGroupArea();
        _groupArea.Visibility = Visibility.Collapsed;

        _contentArea.Children.Add(_startArea);
        _contentArea.Children.Add(_individualLauncher);
        _contentArea.Children.Add(_examArea);
        _contentArea.Children.Add(_groupArea);
        _contentArea.Children.Add(_resultsArea);
    }

    private FrameworkElement BuildStartArea()
    {
        var stack = new StackPanel { Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 600 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.ExamChoosePrompt,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, HorizontalAlignment = HorizontalAlignment.Center };
        var individual = CreateModeCard(AppStrings.ExamModeIndividual, "\uE77B", ShowIndividualLauncher);
        var group = CreateModeCard(AppStrings.ExamModeGroup, "\uE716", () => { _groupMode = true; UpdateExamView(); });
        buttons.Children.Add(individual);
        buttons.Children.Add(group);
        stack.Children.Add(buttons);
        return stack;
    }

    private static Button CreateModeCard(string title, string glyph, Action onClick)
    {
        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new FontIcon { Glyph = glyph, FontSize = 32, Foreground = AppTheme.Accent, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        var btn = new Button
        {
            Content = stack,
            MinWidth = 200,
            MinHeight = 120,
            CornerRadius = new CornerRadius(10),
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => onClick();
        return btn;
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
        _examTab.FontWeight = tab == "exam" ? FontWeights.Bold : FontWeights.Normal;
        _resultsTab.FontWeight = tab == "results" ? FontWeights.Bold : FontWeights.Normal;

        if (tab == "results")
        {
            _resultsArea.Content = BuildResultsContent();
            _startArea.Visibility = Visibility.Collapsed;
            _individualLauncher.Visibility = Visibility.Collapsed;
            _examArea.Visibility = Visibility.Collapsed;
            _groupArea.Visibility = Visibility.Collapsed;
            _resultsArea.Visibility = Visibility.Visible;
            ParkStimulus();
        }
        else
        {
            UpdateExamView();
        }
        NotifyMonitorVisibility();
    }

    private void NotifyMonitorVisibility()
    {
        MonitorVisibilityChanged?.Invoke(this, _tab == "exam" && _examArea.Visibility == Visibility.Visible && _monitor.Visibility == Visibility.Visible);
    }

    // ── Exam view (choice / group / taking / graded) ─────────────────────────

    private void UpdateExamView()
    {
        _resultsArea.Visibility = Visibility.Collapsed;
        // The individual picker owns its own state (shown from ShowIndividualLauncher); any transition
        // through the normal exam view — taking, group, or the start choice — leaves it.
        _individualLauncher.Visibility = Visibility.Collapsed;

        var taking = _vm is not null && (_vm.Result is not null || _vm.IsTakingExam);
        if (taking)
        {
            _startArea.Visibility = Visibility.Collapsed;
            _groupArea.Visibility = Visibility.Collapsed;
            _examArea.Visibility = Visibility.Visible;

            var graded = _vm!.Result is not null;
            _questionPanel.Visibility = graded ? Visibility.Collapsed : Visibility.Visible;
            _breakdownScroll.Visibility = graded ? Visibility.Visible : Visibility.Collapsed;

            if (graded)
            {
                _breakdownScroll.Content = BuildBreakdown(_vm.Result!, _vm.Test, showNewAttempt: true);
                _loadedQuestionId = null;
                ParkStimulus();
                NotifyMonitorVisibility();
                return;
            }

            var q = _vm.Current;
            if (q is not null && q.Id != _loadedQuestionId)
            {
                _loadedQuestionId = q.Id;
                ApplyStimulus(q);
            }
            NotifyMonitorVisibility();
            return;
        }

        // Not taking: either the Individual/Group choice or the group-session panel.
        _examArea.Visibility = Visibility.Collapsed;
        _loadedQuestionId = null;
        ParkStimulus();

        if (_groupMode)
        {
            _startArea.Visibility = Visibility.Collapsed;
            _groupArea.Visibility = Visibility.Visible;
            RefreshGroupView();
        }
        else
        {
            _startArea.Visibility = Visibility.Visible;
            _groupArea.Visibility = Visibility.Collapsed;
        }
        NotifyMonitorVisibility();
    }

    /// <summary>Parks the left pane: monitor visible but stopped, image hidden.</summary>
    private void ParkStimulus()
    {
        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;
        _monitor.Visibility = Visibility.Visible;
        _monitorVm?.SetIsRunning(false);
        if (_examMonitorControls is not null) _examMonitorControls.Visibility = Visibility.Collapsed;
    }

    private void ApplyStimulus(TestQuestion q)
    {
        if (q.Stimulus == QuestionStimulus.Image && TestImageStore.UriFor(q.ImagePath) is { } uri)
        {
            _stimulusImage.Source = new BitmapImage(uri);
            _stimulusImage.Visibility = Visibility.Visible;
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
            if (_examMonitorControls is not null) _examMonitorControls.Visibility = Visibility.Collapsed;
            return;
        }

        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;

        if (q.Stimulus == QuestionStimulus.Ecg && q.PathologyId is { } pathologyId && _rhythmVm is not null && _monitorVm is not null)
        {
            _monitor.Visibility = Visibility.Visible;
            _rhythmVm.SelectRhythm(pathologyId, persist: false);
            _monitorVm.SetLeadSelection(q.LeadList);
            _monitorVm.SetSeriesScheme(q.Scheme);
            _monitorVm.SetIsRunning(true);
            // Show the start/stop + column controls for ECG questions and sync them to the new state.
            if (_examMonitorControls is not null) _examMonitorControls.Visibility = Visibility.Visible;
            _syncExamControls?.Invoke();
        }
        else
        {
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
            if (_examMonitorControls is not null) _examMonitorControls.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The compact start/stop + 1–2 column (lead-layout) control strip pinned to the exam monitor
    /// (B7, customer 28-08). Reuses the monitor's own controls: freeze the running trace and cycle the
    /// column scheme, exactly as on the main monitor.</summary>
    private Border BuildExamMonitorControls()
    {
        var startStop = new Button { MinWidth = 40, Padding = new Thickness(8, 4, 8, 4) };
        var columns = new Button { MinWidth = 40, Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(startStop, AppStrings.ExamMonitorFreeze);
        ToolTipService.SetToolTip(columns, AppStrings.ExamMonitorColumns);

        void Sync()
        {
            var m = _monitorVm;
            startStop.Content = (m?.MonitorMode.IsRunning ?? false) ? "⏹" : "▶";
            columns.Content = SchemeShort(m?.MonitorMode.SeriesScheme ?? SeriesScheme.OneColumn);
        }
        _syncExamControls = Sync;

        startStop.Click += (_, _) => { if (_monitorVm is { } m) { m.SetIsRunning(!m.MonitorMode.IsRunning); Sync(); } };
        columns.Click += (_, _) => { if (_monitorVm is { } m) { m.SetSeriesScheme(NextScheme(m.MonitorMode.SeriesScheme)); Sync(); } };
        Sync();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bar.Children.Add(startStop);
        bar.Children.Add(columns);

        return new Border
        {
            Child = bar,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Background = AppTheme.AppCardBackground,
            BorderBrush = AppTheme.AppCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
        };

        static SeriesScheme NextScheme(SeriesScheme s) => s switch
        {
            SeriesScheme.OneColumn => SeriesScheme.TwoColumn,
            SeriesScheme.TwoColumn => SeriesScheme.Grid,
            _ => SeriesScheme.OneColumn,
        };
        static string SchemeShort(SeriesScheme s) => s switch
        {
            SeriesScheme.OneColumn => AppStrings.MonitorColumnsOneShort,
            SeriesScheme.TwoColumn => AppStrings.MonitorColumnsTwoShort,
            _ => AppStrings.MonitorColumnsGridShort,
        };
    }

    // ── Individual flow ──────────────────────────────────────────────────────

    /// <summary>Swaps the start choice for the course-wide Quick Test picker (ready test / generate over
    /// all course themes). Re-initialized on each entry so the theme + ready-test lists are current.</summary>
    private void ShowIndividualLauncher()
    {
        if (_appVm is null) return;
        _individualLauncher.InitializeCourseMode(
            _appVm,
            AppStrings.ExamModeIndividual,
            AppStrings.QuickCourseSubtitle,
            AppStrings.QuickContinue,
            AppStrings.ExamGroupBack);
        _startArea.Visibility = Visibility.Collapsed;
        _examArea.Visibility = Visibility.Collapsed;
        _groupArea.Visibility = Visibility.Collapsed;
        _resultsArea.Visibility = Visibility.Collapsed;
        _individualLauncher.Visibility = Visibility.Visible;
        ParkStimulus();
    }

    /// <summary>The picker's «back» returns to the Individual / Group choice.</summary>
    private void OnIndividualLauncherBack()
    {
        _individualLauncher.Visibility = Visibility.Collapsed;
        UpdateExamView();
    }

    /// <summary>The picker produced a test — collect who is taking it (roster pick or manual ФИО +
    /// группа), then start the graded attempt.</summary>
    private async void OnIndividualTestChosen(Test test)
    {
        if (_vm is null || _appVm is null) return;
        if (test.Questions.Count == 0) { await InfoAsync(AppStrings.ExamModeIndividual, AppStrings.BankEmpty); return; }
        // Identity is chosen on the launcher now (customer request 28-08-2026); fall back to the dialog only
        // if the launcher couldn't resolve one.
        var student = _individualLauncher.SelectedStudent ?? await ShowStudentDialogAsync();
        if (student is null) return;
        _individualLauncher.Visibility = Visibility.Collapsed;
        _vm.Start(test, student);
        UpdateExamView();
    }

    /// <summary>Collects the exam-taker's identity: a registered-student pick (Full-edition roster,
    /// pre-fills the fields) or manual ФИО + группа. Returns null when cancelled.</summary>
    private async Task<ExamStudentInfo?> ShowStudentDialogAsync()
    {
        if (_appVm is null) return null;

        var fio = new TextBox { Header = AppStrings.ExamFieldFullName, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var group = new TextBox { Header = AppStrings.ExamFieldGroup, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };

        // Registered-student pick-list (Full-edition roster). Choosing an entry pre-fills ФИО + группа;
        // the fields stay editable so manual entry still works. Only shown when the instructor has
        // registered students (see the Students screen / StudentStore).
        var roster = _appVm.StudentStore.List();
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

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        if (studentBox is not null) panel.Children.Add(studentBox);
        panel.Children.Add(fio);
        panel.Children.Add(group);

        var dialog = new ContentDialog
        {
            Title = AppStrings.ExamModeIndividual,
            Content = panel,
            PrimaryButtonText = AppStrings.ExamStart,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = AppTheme.Current,
        };

        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) && !string.IsNullOrWhiteSpace(group.Text);
        fio.TextChanged += (_, _) => Revalidate();
        group.TextChanged += (_, _) => Revalidate();
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return new ExamStudentInfo(fio.Text.Trim(), group.Text.Trim());
    }

    // ── Group flow ─────────────────────────────────────────────────────────--

    private Grid BuildGroupArea()
    {
        var area = new Grid { Padding = new Thickness(24) };

        // Setup: the shared Quick-Test customization card (ready test / generate over all course themes) —
        // identical to the Individual flow. Its Start raises GroupSessionRequested with a per-participant
        // test factory; its Back leaves Group mode. Initialized each time setup is shown (RefreshGroupView).
        _groupLauncher = new QuickTestScreen();

        // Live: QR + url + roster + stop. Two columns (QR left, roster right).
        _groupLive = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var liveGrid = new Grid();
        liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var qrPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 24, 0) };
        qrPanel.Children.Add(new TextBlock { Text = AppStrings.ExamGroupScan, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
        qrPanel.Children.Add(_groupQr);
        _groupUrl = new TextBlock { IsTextSelectionEnabled = true, FontSize = 13, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center };
        qrPanel.Children.Add(_groupUrl);
        var stop = new Button { Content = AppStrings.ExamGroupStop, HorizontalAlignment = HorizontalAlignment.Center };
        stop.Click += (_, _) => OnStopSession();
        qrPanel.Children.Add(stop);
        Grid.SetColumn(qrPanel, 0);
        liveGrid.Children.Add(qrPanel);

        var rosterPanel = new StackPanel { Spacing = 6 };
        _groupRosterCount = new TextBlock { FontWeight = FontWeights.SemiBold };
        rosterPanel.Children.Add(_groupRosterCount);
        _rosterHost = new StackPanel { Spacing = 4 };
        rosterPanel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rosterHost });
        Grid.SetColumn(rosterPanel, 1);
        liveGrid.Children.Add(rosterPanel);

        _groupLive.Children.Add(liveGrid);

        area.Children.Add(_groupLauncher);
        area.Children.Add(_groupLive);
        return area;
    }

    private void RefreshGroupView()
    {
        if (_appVm is null) return;
        var running = _appVm.GroupTestServer.IsRunning;
        _groupLauncher.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _groupLive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (!running)
        {
            // (Re)bind the setup card so its theme + ready-test lists reflect the current course.
            _groupLauncher.InitializeGroupMode(
                _appVm,
                AppStrings.ExamModeGroup,
                AppStrings.QuickCourseSubtitle,
                AppStrings.ExamGroupStart,
                AppStrings.ExamGroupBack);
            return;
        }

        _groupUrl.Text = _appVm.GroupTestServer.Url ?? string.Empty;
        RefreshRoster();
    }

    private async void OnGroupConfigured(GroupTestConfig config)
    {
        if (_appVm is null) return;
        var url = _appVm.GroupTestServer.Start(config);
        if (url is null)
        {
            await InfoAsync(AppStrings.ExamModeGroup, AppStrings.ExamGroupNoNetwork);
            return;
        }
        await SetQrAsync(url);
        RefreshGroupView();
    }

    private void OnGroupLauncherBack()
    {
        _groupMode = false;
        UpdateExamView();
    }

    private void OnStopSession()
    {
        _appVm?.GroupTestServer.Stop();
        RefreshGroupView();
    }

    private void RefreshRoster()
    {
        if (_appVm is null || _rosterHost is null) return;
        var participants = _appVm.GroupTestServer.Participants;
        var finished = participants.Count(p => p.Finished);
        _groupRosterCount.Text = AppStrings.ExamRosterCountFormat(participants.Count, finished);

        _rosterHost.Children.Clear();
        foreach (var p in participants)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{p.Student.FullName} · {p.Student.Group}", TextWrapping = TextWrapping.Wrap });
            var status = p.Finished
                ? new TextBlock
                {
                    Text = $"{p.Result!.CorrectCount}/{p.Result.TotalCount}",
                    Foreground = p.Result.Passed ? AppTheme.Positive : AppTheme.Negative,
                    FontWeight = FontWeights.SemiBold,
                }
                : new TextBlock { Text = AppStrings.ExamRosterInProgress, Opacity = 0.6 };
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            _rosterHost.Children.Add(row);
        }
    }

    private async Task SetQrAsync(string url)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);

            var bmp = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(png);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                await bmp.SetSourceAsync(stream);
            }
            _groupQr.Source = bmp;
        }
        catch { /* QR is best-effort; the URL text is still shown */ }
    }

    private async Task InfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
            RequestedTheme = AppTheme.Current,
        };
        await dialog.ShowAsync();
    }

    // ── Grading breakdown (shared by post-submit + saved results) ───────────

    private UIElement BuildBreakdown(ExamResult result, Test? test, bool showNewAttempt)
    {
        var lookup = test?.Questions.ToDictionary(q => q.Id);
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(12) };
        panel.Children.Add(BuildResultBanner(result));

        var number = 1;
        foreach (var r in result.Questions)
        {
            TestQuestion? q = null;
            lookup?.TryGetValue(r.QuestionId, out q);
            panel.Children.Add(BuildGradedQuestion(r, q, number++));
        }

        if (showNewAttempt)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
            // A3: when launched from the Learning Scale «Сдать», offer a return to the dashboard (the result
            // is already saved, so the block reflects it).
            if (_returnToLearningScale is not null)
            {
                var back = new Button { Content = AppStrings.ExamBackToLearningScale };
                back.Click += (_, _) => ReturnToLearningScale();
                buttons.Children.Add(back);
            }
            var newBtn = new Button { Content = AppStrings.ExamNewAttempt };
            newBtn.Click += (_, _) => { _vm?.Reset(); UpdateExamView(); };
            buttons.Children.Add(newBtn);
            panel.Children.Add(buttons);
        }
        return panel;
    }

    /// <summary>Returns to the Learning Scale dashboard (re-selecting the student the exam was launched for),
    /// after a key-test attempt launched from there (A3).</summary>
    private void ReturnToLearningScale()
    {
        if (_appVm is null) return;
        _appVm.PendingLearningScaleStudent = _returnToLearningScale;
        _returnToLearningScale = null;
        _vm?.Reset();
        var target = _appVm.OperatingModes.FirstOrDefault(m => m.Id == OperatingMode.LearningScale)
                     ?? new OperatingModeModel(OperatingMode.LearningScale);
        _ = _appVm.RequestOperatingModeAsync(target);
    }

    private static UIElement BuildGradedQuestion(ExamQuestionResult r, TestQuestion? q, int number)
    {
        var block = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = r.IsCorrect ? "✓" : "✗",
            Foreground = r.IsCorrect ? AppTheme.Positive : AppTheme.Negative,
            FontWeight = FontWeights.Bold,
        });
        header.Children.Add(new TextBlock
        {
            Text = q is null ? $"{number}." : $"{number}. {q.Text}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        block.Children.Add(header);

        // Show the correct option and (if different/wrong) the student's pick.
        var ids = q is null
            ? new[] { r.Correct, r.Selected }.Where(id => id is not null).Select(id => id!).Distinct()
            : q.Options.Select(o => o.Id).Where(id => id == r.Correct || id == r.Selected);

        foreach (var id in ids)
        {
            var isCorrect = id == r.Correct;
            var brush = isCorrect ? AppTheme.Positive : AppTheme.Negative;
            var text = q?.Options.FirstOrDefault(o => o.Id == id)?.Text ?? id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = isCorrect ? "✓" : "✗", Foreground = brush, Width = 14 });
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush });
            block.Children.Add(row);
        }

        if (r.Selected is null)
        {
            block.Children.Add(new TextBlock
            {
                Text = AppStrings.ExamUnanswered,
                Margin = new Thickness(16, 0, 0, 0),
                Opacity = 0.7,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
            });
        }
        return block;
    }

    private static FrameworkElement BuildResultBanner(ExamResult res)
    {
        var accent = res.Passed ? AppTheme.PositiveColor : AppTheme.NegativeColor;
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = res.Passed ? AppStrings.ExamPassed : AppStrings.ExamFailed,
            FontWeight = FontWeights.Bold,
            Foreground = res.Passed ? AppTheme.Positive : AppTheme.Negative,
        });
        stack.Children.Add(new TextBlock { Text = AppStrings.ExamScoreFormat(res.CorrectCount, res.TotalCount) });
        stack.Children.Add(new TextBlock { Text = $"{res.Student.FullName} · {res.Student.Group}", Opacity = 0.7, FontSize = 12 });
        border.Child = stack;
        return border;
    }

    // ── Results tab ────────────────────────────────────────────────────────

    private UIElement BuildResultsContent()
    {
        if (_appVm is null) return new Grid();
        var entries = _appVm.ExamResultStore.ListEntries();
        if (entries.Count == 0) return Placeholder(AppStrings.ExamResultsEmpty);

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

        ExamResultStore.Entry? Selected() => (list.SelectedItem as ListViewItem)?.Tag as ExamResultStore.Entry;

        list.SelectionChanged += (_, _) =>
        {
            var entry = Selected();
            editBtn.IsEnabled = entry is not null;
            deleteBtn.IsEnabled = entry is not null;
            if (entry is null) { detail.Content = null; return; }
            var test = _appVm.TestRepository.Test(entry.Result.TestId);
            detail.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = BuildBreakdown(entry.Result, test, showNewAttempt: false),
            };
        };

        editBtn.Click += async (_, _) => { if (Selected() is { } e) await EditResultAsync(e); };
        deleteBtn.Click += async (_, _) => { if (Selected() is { } e) await DeleteResultAsync(e); };
        clearBtn.Click += async (_, _) => await ClearResultsAsync(entries.Count);

        list.SelectedIndex = 0;
        return grid;
    }

    private async Task DeleteResultAsync(ExamResultStore.Entry entry)
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
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _appVm.ExamResultStore.Delete(entry.Path);
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
            RequestedTheme = AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _appVm.ExamResultStore.Clear();
        _resultsArea.Content = BuildResultsContent();
    }

    // Edits an attempt's student identity and grade in place. Per-question verdicts (the breakdown) are
    // left untouched — this is a manual override / appeal, not a re-grade — so the summary may legitimately
    // diverge from the per-question tally after a correction.
    private async Task EditResultAsync(ExamResultStore.Entry entry)
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
        FieldFocus.SpinButtonsOnlyWhenFocused(correct);
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
            RequestedTheme = AppTheme.Current,
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
        var ok = _appVm.ExamResultStore.Overwrite(entry.Path, edited);
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
            RequestedTheme = AppTheme.Current,
        };
        await dialog.ShowAsync();
    }

    private static UIElement BuildResultListItem(ExamResult r)
    {
        // Roomier line spacing + an inset margin so the three lines aren't jammed together or against the
        // row edges. Margin (not an opaque card background) keeps the ListView's selection highlight,
        // which fills the row behind the inset content, visible.
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(4, 8, 4, 8) };
        panel.Children.Add(new TextBlock { Text = r.Student.FullName, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = $"{r.Student.Group} · {r.TestTitle} · {r.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm}",
            Opacity = 0.8,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{(r.Passed ? AppStrings.ExamPassed : AppStrings.ExamFailed)} — {AppStrings.ExamScoreFormat(r.CorrectCount, r.TotalCount)}",
            Foreground = r.Passed ? AppTheme.Positive : AppTheme.Negative,
            FontSize = 12,
        });
        return panel;
    }

    private static UIElement Placeholder(string text) => new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = AppTheme.TextSecondary,
    };

    private async Task ReturnToLectureAsync()
    {
        if (_appVm is null) return;
        if (_vm is { IsTakingExam: true })
        {
            var dialog = new ContentDialog
            {
                Title = AppStrings.TestAbort,
                Content = AppStrings.TestAbortConfirm,
                PrimaryButtonText = AppStrings.TestAbort,
                CloseButtonText = AppStrings.CommonCancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = AppTheme.Current,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _appVm.PreserveCourseSelection = true;
        var target = _appVm.OperatingModes.FirstOrDefault(m => m.Id == OperatingMode.Teaching)
                     ?? new OperatingModeModel(OperatingMode.Teaching);
        await _appVm.RequestOperatingModeAsync(target);
    }
}
