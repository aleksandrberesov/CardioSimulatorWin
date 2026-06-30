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
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private string? _loadedQuestionId;

    private readonly MonitorView _monitor = new();
    private readonly Image _stimulusImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly ExamQuestionPanel _questionPanel = new();
    private readonly Grid _root = new();
    private Button _examTab = null!;
    private Button _resultsTab = null!;
    private string _tab = "exam";

    private readonly Grid _contentArea = new();
    private FrameworkElement _startArea = null!;
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
    private StackPanel _groupSetup = null!;
    private StackPanel _groupLive = null!;
    private ComboBox _groupCount = null!;
    private ComboBox _groupTheme = null!;
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

        vm.StateChanged += OnVmStateChanged;
        appVm.GroupTestServer.ParticipantsChanged += OnParticipantsChanged;
        Unloaded += (_, _) =>
        {
            vm.StateChanged -= OnVmStateChanged;
            appVm.GroupTestServer.ParticipantsChanged -= OnParticipantsChanged;
        };

        // Re-attach to a session that is already running (e.g. after switching modes and back).
        if (appVm.GroupTestServer.IsRunning)
        {
            _groupMode = true;
            if (appVm.GroupTestServer.Url is { } url) _ = SetQrAsync(url);
        }

        ShowTab("exam");
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

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(12, 8, 12, 8) };
        _examTab = TabButton(AppStrings.ExamTabExam, () => ShowTab("exam"));
        _resultsTab = TabButton(AppStrings.ExamTabResults, () => ShowTab("results"));
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
        _startArea = BuildStartArea();

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
        _contentArea.Children.Add(_examArea);
        _contentArea.Children.Add(_groupArea);
        _contentArea.Children.Add(_resultsArea);
    }

    private FrameworkElement BuildStartArea()
    {
        var stack = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560 };
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.ExamChoosePrompt,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        var individual = new Button { Content = AppStrings.ExamModeIndividual, MinWidth = 200, MinHeight = 56, FontSize = 16 };
        individual.Click += async (_, _) => await OnIndividualAsync();
        var group = new Button { Content = AppStrings.ExamModeGroup, MinWidth = 200, MinHeight = 56, FontSize = 16 };
        group.Click += (_, _) => { _groupMode = true; UpdateExamView(); };
        buttons.Children.Add(individual);
        buttons.Children.Add(group);
        stack.Children.Add(buttons);
        return stack;
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
            _examArea.Visibility = Visibility.Collapsed;
            _groupArea.Visibility = Visibility.Collapsed;
            _resultsArea.Visibility = Visibility.Visible;
            ParkStimulus();
        }
        else
        {
            UpdateExamView();
        }
    }

    // ── Exam view (choice / group / taking / graded) ─────────────────────────

    private void UpdateExamView()
    {
        _resultsArea.Visibility = Visibility.Collapsed;

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
                return;
            }

            var q = _vm.Current;
            if (q is not null && q.Id != _loadedQuestionId)
            {
                _loadedQuestionId = q.Id;
                ApplyStimulus(q);
            }
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
    }

    /// <summary>Parks the left pane: monitor visible but stopped, image hidden.</summary>
    private void ParkStimulus()
    {
        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;
        _monitor.Visibility = Visibility.Visible;
        _monitorVm?.SetIsRunning(false);
    }

    private void ApplyStimulus(TestQuestion q)
    {
        if (q.Stimulus == QuestionStimulus.Image && TestImageStore.UriFor(q.ImagePath) is { } uri)
        {
            _stimulusImage.Source = new BitmapImage(uri);
            _stimulusImage.Visibility = Visibility.Visible;
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
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
        }
        else
        {
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm?.SetIsRunning(false);
        }
    }

    // ── Individual flow ──────────────────────────────────────────────────────

    private async Task OnIndividualAsync()
    {
        if (_vm is null || _appVm is null) return;
        var picked = await ShowIndividualDialogAsync();
        if (picked is null) return;
        _vm.Start(picked.Value.test, picked.Value.student);
        UpdateExamView();
    }

    private async Task<(ExamStudentInfo student, Test test)?> ShowIndividualDialogAsync()
    {
        var fio = new TextBox { Header = AppStrings.ExamFieldFullName, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var group = new TextBox { Header = AppStrings.ExamFieldGroup, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };

        var genRadio = new RadioButton { Content = AppStrings.ExamSourceGenerate, GroupName = "src", IsChecked = true };
        var savedRadio = new RadioButton { Content = AppStrings.ExamSourceSaved, GroupName = "src" };

        // Generate sub-panel: count + theme.
        var countBox = new ComboBox { Header = AppStrings.ExamCount, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in TestGenerator.CountOptions) countBox.Items.Add(new ComboBoxItem { Content = c.ToString(), Tag = c });
        countBox.SelectedIndex = 0;
        var themeBox = new ComboBox { Header = AppStrings.ExamTheme, HorizontalAlignment = HorizontalAlignment.Stretch };
        themeBox.Items.Add(new ComboBoxItem { Content = AppStrings.BankFilterAll, Tag = null });
        foreach (var t in _appVm!.Themes.Read()) themeBox.Items.Add(new ComboBoxItem { Content = t, Tag = t });
        themeBox.SelectedIndex = 0;
        var genPanel = new StackPanel { Spacing = 8 };
        genPanel.Children.Add(countBox);
        genPanel.Children.Add(themeBox);

        // Saved-test sub-panel.
        var testBox = new ComboBox { Header = AppStrings.ExamFieldTest, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var t in _appVm.TestRepository.Tests)
            testBox.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.TestId });
        if (testBox.Items.Count > 0) testBox.SelectedIndex = 0;
        var savedPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        savedPanel.Children.Add(testBox);

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(fio);
        panel.Children.Add(group);
        panel.Children.Add(genRadio);
        panel.Children.Add(genPanel);
        panel.Children.Add(savedRadio);
        panel.Children.Add(savedPanel);

        var dialog = new ContentDialog
        {
            Title = AppStrings.ExamModeIndividual,
            Content = panel,
            PrimaryButtonText = AppStrings.ExamStart,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        bool BankHasQuestions() => _appVm.QuestionBank.Questions.Count > 0;
        void Revalidate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(fio.Text) &&
            !string.IsNullOrWhiteSpace(group.Text) &&
            (genRadio.IsChecked == true ? BankHasQuestions() : testBox.SelectedItem is ComboBoxItem);

        void OnSourceChanged()
        {
            genPanel.Visibility = genRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            savedPanel.Visibility = savedRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Revalidate();
        }
        genRadio.Checked += (_, _) => OnSourceChanged();
        savedRadio.Checked += (_, _) => OnSourceChanged();
        fio.TextChanged += (_, _) => Revalidate();
        group.TextChanged += (_, _) => Revalidate();
        testBox.SelectionChanged += (_, _) => Revalidate();
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var student = new ExamStudentInfo(fio.Text.Trim(), group.Text.Trim());

        if (genRadio.IsChecked == true)
        {
            var count = (countBox.SelectedItem as ComboBoxItem)?.Tag is int c ? c : 10;
            var theme = (themeBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var test = TestGenerator.Generate(_appVm.QuestionBank.Questions, count, theme, Random.Shared);
            if (test.Questions.Count == 0) return null;
            return (student, test);
        }

        if (testBox.SelectedItem is not ComboBoxItem item || item.Tag is not string testId) return null;
        if (_appVm.TestRepository.Test(testId) is not { } saved || saved.Questions.Count == 0) return null;
        return (student, saved);
    }

    // ── Group flow ─────────────────────────────────────────────────────────--

    private Grid BuildGroupArea()
    {
        var area = new Grid { Padding = new Thickness(24) };

        // Setup: count + theme + start.
        _groupSetup = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        _groupSetup.Children.Add(new TextBlock { Text = AppStrings.ExamGroupSetupTitle, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _groupCount = new ComboBox { Header = AppStrings.ExamCount, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in TestGenerator.CountOptions) _groupCount.Items.Add(new ComboBoxItem { Content = c.ToString(), Tag = c });
        _groupCount.SelectedIndex = 0;
        _groupTheme = new ComboBox { Header = AppStrings.ExamTheme, HorizontalAlignment = HorizontalAlignment.Stretch };
        _groupSetup.Children.Add(_groupCount);
        _groupSetup.Children.Add(_groupTheme);

        var setupButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var start = new Button { Content = AppStrings.ExamGroupStart };
        start.Click += async (_, _) => await OnStartSessionAsync();
        var back = new Button { Content = AppStrings.ExamGroupBack };
        back.Click += (_, _) => { _groupMode = false; UpdateExamView(); };
        setupButtons.Children.Add(start);
        setupButtons.Children.Add(back);
        _groupSetup.Children.Add(setupButtons);

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

        area.Children.Add(_groupSetup);
        area.Children.Add(_groupLive);
        return area;
    }

    private void RefreshGroupView()
    {
        if (_appVm is null) return;
        var running = _appVm.GroupTestServer.IsRunning;
        _groupSetup.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _groupLive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (!running)
        {
            // (Re)populate the theme picker from the current catalog.
            _groupTheme.Items.Clear();
            _groupTheme.Items.Add(new ComboBoxItem { Content = AppStrings.BankFilterAll, Tag = null });
            foreach (var t in _appVm.Themes.Read()) _groupTheme.Items.Add(new ComboBoxItem { Content = t, Tag = t });
            _groupTheme.SelectedIndex = 0;
            return;
        }

        _groupUrl.Text = _appVm.GroupTestServer.Url ?? string.Empty;
        RefreshRoster();
    }

    private async Task OnStartSessionAsync()
    {
        if (_appVm is null) return;
        var count = (_groupCount.SelectedItem as ComboBoxItem)?.Tag is int c ? c : 10;
        var theme = (_groupTheme.SelectedItem as ComboBoxItem)?.Tag as string;

        if (_appVm.QuestionBank.Questions.Count == 0)
        {
            await InfoAsync(AppStrings.ExamModeGroup, AppStrings.BankEmpty);
            return;
        }

        var url = _appVm.GroupTestServer.Start(count, theme);
        if (url is null)
        {
            await InfoAsync(AppStrings.ExamModeGroup, AppStrings.ExamGroupNoNetwork);
            return;
        }
        await SetQrAsync(url);
        RefreshGroupView();
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
            var newBtn = new Button { Content = AppStrings.ExamNewAttempt, Margin = new Thickness(0, 8, 0, 0) };
            newBtn.Click += (_, _) => { _vm?.Reset(); UpdateExamView(); };
            panel.Children.Add(newBtn);
        }
        return panel;
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
        var results = _appVm.ExamResultStore.List();
        if (results.Count == 0) return Placeholder(AppStrings.ExamResultsEmpty);

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
            if (list.SelectedItem is ListViewItem item && item.Tag is ExamResult r)
            {
                var test = _appVm.TestRepository.Test(r.TestId);
                detail.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = BuildBreakdown(r, test, showNewAttempt: false),
                };
            }
        };
        list.SelectedIndex = 0;
        return grid;
    }

    private static UIElement BuildResultListItem(ExamResult r)
    {
        var panel = new StackPanel { Spacing = 2 };
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
}
