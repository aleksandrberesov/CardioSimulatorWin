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
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;
using Windows.UI;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Testing mode: self-assessment quiz flow. Offers choice between <b>Индивидуальное</b> (take on this PC
/// via <see cref="QuickTestScreen"/>) and <b>Групповое</b> (a QR to the LAN <see cref="GroupTestServer"/> is shown;
/// students join via phone browsers).
/// </summary>
public sealed class TestingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly Image _stimulusImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly EcgAssemblyControl _assembly = new() { Visibility = Visibility.Collapsed };
    private readonly TestQuestionPanel _questionPanel = new();
    private readonly TestViewModel _testVm = new();
    private readonly QuickTestScreen _launcher = new();
    private readonly Grid _testHost = new();

    private FrameworkElement _startArea = null!;
    private bool _individualMode;
    private bool _groupMode;

    // Group session UI controls.
    private Grid _groupArea = null!;
    private QuickTestScreen _groupLauncher = null!;
    private StackPanel _groupLive = null!;
    private readonly Image _groupQr = new() { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center };
    private TextBlock _groupUrl = null!;
    private TextBlock _groupRosterCount = null!;
    private StackPanel _rosterHost = null!;

    private AppViewModel? _appVm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private string? _loadedQuestionId;

    /// <summary>Raised when the ECG monitor's visibility changes (e.g. toggled for ECG vs image/assembly questions).</summary>
    public event EventHandler<bool>? MonitorVisibilityChanged;

    public TestingScreen()
    {
        // Taking view: monitor/stimulus/assembly on left, question panel on right.
        _testHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _testHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var left = new Grid();
        left.Children.Add(_monitor);
        _stimulusImage.Visibility = Visibility.Collapsed;
        left.Children.Add(_stimulusImage);
        left.Children.Add(_assembly);
        Grid.SetColumn(left, 0);
        _testHost.Children.Add(left);

        Grid.SetColumn(_questionPanel, 1);
        _testHost.Children.Add(_questionPanel);

        _startArea = BuildStartArea();
        _groupArea = BuildGroupArea();

        // Root container: Start choice, Individual launcher, Group session view, and Test taking view.
        // Toggled via Visibility to preserve Win2D monitor control lifecycle.
        var root = new Grid();
        root.Children.Add(_startArea);
        _launcher.Visibility = Visibility.Collapsed;
        root.Children.Add(_launcher);
        _groupArea.Visibility = Visibility.Collapsed;
        root.Children.Add(_groupArea);
        _testHost.Visibility = Visibility.Collapsed;
        root.Children.Add(_testHost);
        Content = root;
    }

    public void Initialize(
        MonitorViewModel monitorVm,
        RhythmViewModel rhythmVm,
        AppViewModel appVm)
    {
        _appVm = appVm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;

        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;

        _questionPanel.Bind(_testVm, appVm.TestRepository, appVm);

        _launcher.InitializeCourseMode(
            appVm,
            AppStrings.ExamModeIndividual,
            AppStrings.QuickCourseSubtitle,
            AppStrings.QuickStart,
            AppStrings.ExamGroupBack);
        _launcher.TestStartRequested += OnLauncherStart;
        _launcher.BackToLectureRequested += OnLauncherBack;

        // The Group session reuses the same customization card as Individual (ready test / generate).
        _groupLauncher.GroupSessionRequested += OnGroupConfigured;
        _groupLauncher.BackToLectureRequested += OnGroupLauncherBack;

        _assembly.PlacementChanged += () => _testVm.NotifyAssemblyChanged();
        _assembly.LeadChangeRequested += OnAssemblyLeadChangeRequested;

        _testVm.StateChanged += OnTestStateChanged;
        appVm.GroupTestServer.ParticipantsChanged += OnParticipantsChanged;

        Unloaded += (_, _) =>
        {
            _testVm.StateChanged -= OnTestStateChanged;
            appVm.GroupTestServer.ParticipantsChanged -= OnParticipantsChanged;
            _launcher.TestStartRequested -= OnLauncherStart;
            _launcher.BackToLectureRequested -= OnLauncherBack;
            _groupLauncher.GroupSessionRequested -= OnGroupConfigured;
            _groupLauncher.BackToLectureRequested -= OnGroupLauncherBack;
        };

        if (appVm.GroupTestServer.IsRunning)
        {
            _groupMode = true;
            if (appVm.GroupTestServer.Url is { } url) _ = SetQrAsync(url);
        }

        OnTestStateChanged();

        if (appVm.PendingTest is { } pending)
        {
            appVm.PendingTest = null;
            _individualMode = true;
            _testVm.Start(pending);
        }
    }

    private void OnLauncherStart(Test test) => _testVm.Start(test);

    private void OnLauncherBack()
    {
        _individualMode = false;
        OnTestStateChanged();
    }

    private void OnParticipantsChanged() => DispatcherQueue.TryEnqueue(RefreshRoster);

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
        var group = CreateModeCard(AppStrings.ExamModeGroup, "\uE716", () => { _groupMode = true; OnTestStateChanged(); });
        buttons.Children.Add(individual);
        buttons.Children.Add(group);
        stack.Children.Add(buttons);
        return stack;
    }

    private void ShowIndividualLauncher()
    {
        if (_appVm is null) return;
        _launcher.InitializeCourseMode(
            _appVm,
            AppStrings.ExamModeIndividual,
            AppStrings.QuickCourseSubtitle,
            AppStrings.QuickStart,
            AppStrings.ExamGroupBack);
        _individualMode = true;
        _groupMode = false;
        OnTestStateChanged();
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

    private Grid BuildGroupArea()
    {
        var area = new Grid { Padding = new Thickness(24) };

        // Setup: the shared Quick-Test customization card (ready test / generate over all course themes) —
        // identical to the Individual flow. Its Start raises GroupSessionRequested with a per-participant
        // test factory; its Back leaves Group mode. Initialized each time setup is shown (RefreshGroupView).
        _groupLauncher = new QuickTestScreen();

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
        OnTestStateChanged();
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
        catch { /* QR is best-effort; URL text is shown */ }
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

    private void OnTestStateChanged()
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        var showTest = _testVm.HasActiveTest || _testVm.Finished;
        _testHost.Visibility = showTest ? Visibility.Visible : Visibility.Collapsed;

        if (showTest)
        {
            _startArea.Visibility = Visibility.Collapsed;
            _launcher.Visibility = Visibility.Collapsed;
            _groupArea.Visibility = Visibility.Collapsed;

            var question = _testVm.Current;
            if (_testVm.HasActiveTest && question is not null)
            {
                if (question.Id != _loadedQuestionId)
                {
                    _loadedQuestionId = question.Id;
                    ApplyStimulus(question);
                }
                if (question.IsAssembly)
                    _assembly.SetAttempt(_testVm.Assembly, _testVm.Revealed);
            }
            else
            {
                _loadedQuestionId = null;
                _stimulusImage.Source = null;
                _stimulusImage.Visibility = Visibility.Collapsed;
                _assembly.Visibility = Visibility.Collapsed;
                _assembly.SetAttempt(null, false);
                _monitor.Visibility = Visibility.Visible;
                _monitorVm.SetIsRunning(false);
            }
        }
        else
        {
            _loadedQuestionId = null;
            _stimulusImage.Source = null;
            _stimulusImage.Visibility = Visibility.Collapsed;
            _assembly.Visibility = Visibility.Collapsed;
            _assembly.SetAttempt(null, false);
            _monitor.Visibility = Visibility.Visible;
            _monitorVm.SetIsRunning(false);

            if (_groupMode)
            {
                _startArea.Visibility = Visibility.Collapsed;
                _launcher.Visibility = Visibility.Collapsed;
                _groupArea.Visibility = Visibility.Visible;
                RefreshGroupView();
            }
            else if (_individualMode)
            {
                _startArea.Visibility = Visibility.Collapsed;
                _groupArea.Visibility = Visibility.Collapsed;
                _launcher.Visibility = Visibility.Visible;
            }
            else
            {
                _startArea.Visibility = Visibility.Visible;
                _launcher.Visibility = Visibility.Collapsed;
                _groupArea.Visibility = Visibility.Collapsed;
            }
        }

        MonitorVisibilityChanged?.Invoke(this, _testHost.Visibility == Visibility.Visible && _monitor.Visibility == Visibility.Visible);
    }

    /// <summary>
    /// The student picked a different lead in the assembly display bar. The baked parts are one lead's
    /// snapshot, so honour it by re-slicing the source rhythm for the new lead (same part count) and
    /// pushing a fresh attempt. Silently ignored when the question carries no source rhythm, the re-slice
    /// fails, or the answer is already revealed (the bar hides the lead control when re-slicing is
    /// impossible, so this is only a defensive guard).
    /// </summary>
    private void OnAssemblyLeadChangeRequested(Lead lead)
    {
        if (_appVm is null || _monitorVm is null) return;
        if (_testVm.Assembly?.Spec is not { } spec) return;
        if (string.IsNullOrWhiteSpace(spec.SourcePathologyId)) return;

        var fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
        var rebuilt = EcgAssemblyBuilder.Build(_appVm.Repository, spec.SourcePathologyId!, lead, spec.PartCount, fs);
        if (rebuilt is not { IsComplete: true }) return;

        _testVm.ReplaceAssembly(rebuilt);
        _assembly.SetAttempt(_testVm.Assembly, _testVm.Revealed);
    }

    private void ApplyStimulus(TestQuestion question)
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        if (question.IsAssembly)
        {
            _assembly.Visibility = Visibility.Visible;
            _assembly.SetAttempt(_testVm.Assembly, _testVm.Revealed);
            _stimulusImage.Source = null;
            _stimulusImage.Visibility = Visibility.Collapsed;
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm.SetIsRunning(false);
            return;
        }
        _assembly.Visibility = Visibility.Collapsed;

        if (question.Stimulus == QuestionStimulus.Image && TestImageStore.UriFor(question.ImagePath) is { } uri)
        {
            _stimulusImage.Source = new BitmapImage(uri);
            _stimulusImage.Visibility = Visibility.Visible;
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm.SetIsRunning(false);
            return;
        }

        _stimulusImage.Source = null;
        _stimulusImage.Visibility = Visibility.Collapsed;

        if (question.Stimulus == QuestionStimulus.Ecg && question.PathologyId is { } pathologyId)
        {
            _monitor.Visibility = Visibility.Visible;
            _rhythmVm.SelectRhythm(pathologyId, persist: false);
            _monitorVm.SetLeadSelection(question.LeadList);
            _monitorVm.SetSeriesScheme(question.Scheme);
            _monitorVm.SetIsRunning(true);
        }
        else
        {
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm.SetIsRunning(false);
        }
    }

    private async Task ReturnToLectureAsync()
    {
        if (_appVm is null) return;
        _appVm.PreserveCourseSelection = true;
        var target = _appVm.OperatingModes.FirstOrDefault(m => m.Id == OperatingMode.Teaching)
                     ?? new OperatingModeModel(OperatingMode.Teaching);
        await _appVm.RequestOperatingModeAsync(target);
    }
}
