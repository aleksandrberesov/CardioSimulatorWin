using System;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Testing mode: a self-assessment quiz. Its entry view is the shared <see cref="QuickTestScreen"/>
/// launcher in course-wide mode — the same «ready test / generate» card the post-lecture Quick Test
/// uses, but scoped to all course themes (a theme selector, no single lecture). Choosing or generating
/// a test swaps to the taking view: the left pane shows the question's stimulus — a live ECG on the
/// monitor, or (for an image question) the picture in the monitor's place — and the
/// <see cref="TestQuestionPanel"/> — the prototype's question / options / comment flow — is on the
/// right. The monitor is never removed from the tree (its Win2D canvas tears down on Unloaded), only
/// toggled via <see cref="UIElement.Visibility"/>. Net-new on both platforms (Android's TestingScreen
/// is a placeholder).
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

    private AppViewModel? _appVm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private string? _loadedQuestionId;

    public TestingScreen()
    {
        // Taking view: the monitor, image stimulus and assembly workspace share the left column (one
        // visible at a time depending on the current question's kind); the question / answer panel is
        // on the right.
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

        // Root: the course-wide launcher (entry) over the taking view; one visible at a time. The
        // taking view stays in the tree (only collapsed) so the monitor's Win2D canvas is never torn down.
        var root = new Grid();
        root.Children.Add(_launcher);
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

        // Entry launcher: the Quick Test card, course-wide. "Back to lecture" is offered only while a
        // course is selected (mirrors the picker's old behaviour); otherwise a single full-width Start.
        _launcher.InitializeCourseMode(
            appVm,
            AppStrings.ModeName(OperatingMode.Testing),
            AppStrings.QuickCourseSubtitle,
            AppStrings.QuickStart,
            appVm.SelectedCourseId is not null ? AppStrings.QuickBackToLecture : null);
        _launcher.TestStartRequested += OnLauncherStart;
        _launcher.BackToLectureRequested += OnLauncherBack;

        // Placing/removing a piece in the workspace re-broadcasts state so the panel's Check button and
        // the workspace both refresh.
        _assembly.PlacementChanged += () => _testVm.NotifyAssemblyChanged();

        _testVm.StateChanged += OnTestStateChanged;
        Unloaded += (_, _) =>
        {
            _testVm.StateChanged -= OnTestStateChanged;
            _launcher.TestStartRequested -= OnLauncherStart;
            _launcher.BackToLectureRequested -= OnLauncherBack;
        };

        OnTestStateChanged();

        // Post-lecture Quick Test handoff: if a test was queued (a picked ready test or a freshly
        // generated one), start it immediately instead of showing the launcher. One-shot — cleared here.
        if (appVm.PendingTest is { } pending)
        {
            appVm.PendingTest = null;
            _testVm.Start(pending);
        }
    }

    private void OnLauncherStart(Test test) => _testVm.Start(test);

    private async void OnLauncherBack() => await ReturnToLectureAsync();

    /// <summary>Mirrors the current question's stimulus onto the left pane — loading its bound ECG or
    /// image once per question (not on every answer/tick) so the visual matches what is being asked —
    /// and toggles between the launcher (no active test) and the taking view.</summary>
    private void OnTestStateChanged()
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        // The taking view is shown while a test is in progress or on the finished score screen; the
        // launcher shows before a test starts and after the score screen's «choose another» (Close).
        var showTest = _testVm.HasActiveTest || _testVm.Finished;
        _testHost.Visibility = showTest ? Visibility.Visible : Visibility.Collapsed;
        _launcher.Visibility = showTest ? Visibility.Collapsed : Visibility.Visible;

        var question = _testVm.Current;
        if (_testVm.HasActiveTest && question is not null)
        {
            if (question.Id != _loadedQuestionId)
            {
                _loadedQuestionId = question.Id;
                ApplyStimulus(question);
            }
            // The workspace mirrors live placement/reveal state, so refresh it on every state change.
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

    private void ApplyStimulus(TestQuestion question)
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        // «Собери ЭКГ»: the left pane becomes the assembly workspace; no monitor/image.
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
            // Text-only: nothing on the left, monitor parked (kept in the tree, just collapsed).
            _monitor.Visibility = Visibility.Collapsed;
            _monitorVm.SetIsRunning(false);
        }
    }

    /// <summary>Returns to Teaching mode, preserving the selected course (the launcher's «back to
    /// lecture»).</summary>
    private async Task ReturnToLectureAsync()
    {
        if (_appVm is null) return;
        _appVm.PreserveCourseSelection = true;
        var target = _appVm.OperatingModes.FirstOrDefault(m => m.Id == OperatingMode.Teaching)
                     ?? new OperatingModeModel(OperatingMode.Teaching);
        await _appVm.RequestOperatingModeAsync(target);
    }
}
