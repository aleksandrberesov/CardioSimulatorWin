using CardioSimulator.App.Controls;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Testing mode: a self-assessment quiz. The monitor is on the left (no control panel — it is driven
/// by the active question, not the student) and the <see cref="TestQuestionPanel"/> — the prototype's
/// question / options / comment flow — is on the right. Advancing to a question loads its bound ECG so
/// the student reads the trace before answering. Net-new on both platforms (Android's TestingScreen is
/// a placeholder).
/// </summary>
public sealed class TestingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly TestQuestionPanel _questionPanel = new();
    private readonly TestViewModel _testVm = new();

    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private string? _loadedQuestionId;

    public TestingScreen()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        // Left: the monitor fills the column (no control panel in this mode).
        Grid.SetColumn(_monitor, 0);
        grid.Children.Add(_monitor);

        // Right: the question / answer panel.
        Grid.SetColumn(_questionPanel, 1);
        grid.Children.Add(_questionPanel);

        Content = grid;
    }

    public void Initialize(
        MonitorViewModel monitorVm,
        RhythmViewModel rhythmVm,
        TestRepository testRepository,
        DomainLanguage displayLanguage)
    {
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;

        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = displayLanguage;

        _questionPanel.Bind(_testVm, testRepository);

        _testVm.StateChanged += OnTestStateChanged;
        Unloaded += (_, _) => _testVm.StateChanged -= OnTestStateChanged;

        OnTestStateChanged();
    }

    /// <summary>Mirrors the current question onto the monitor — loading its bound ECG once per
    /// question (not on every answer/tick) so the trace matches what is being asked.</summary>
    private void OnTestStateChanged()
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        var question = _testVm.Current;
        if (_testVm.HasActiveTest && question is not null)
        {
            if (question.Id != _loadedQuestionId)
            {
                _loadedQuestionId = question.Id;
                if (question.PathologyId is { } pathologyId)
                {
                    _rhythmVm.SelectRhythm(pathologyId, persist: false);
                    _monitorVm.SetLeadSelection(question.LeadList);
                    _monitorVm.SetSeriesScheme(question.Scheme);
                }
                _monitorVm.SetIsRunning(true);
            }
        }
        else
        {
            _loadedQuestionId = null;
            _monitorVm.SetIsRunning(false);
        }
    }
}
