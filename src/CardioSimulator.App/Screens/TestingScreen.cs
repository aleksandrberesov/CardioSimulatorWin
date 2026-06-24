using System;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
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
/// Testing mode: a self-assessment quiz. The left pane shows the question's stimulus — a live ECG on
/// the monitor, or (for an image question) the picture in the monitor's place — and the
/// <see cref="TestQuestionPanel"/> — the prototype's question / options / comment flow — is on the
/// right. The monitor is never removed from the tree (its Win2D canvas tears down on Unloaded), only
/// toggled via <see cref="UIElement.Visibility"/>. Net-new on both platforms (Android's TestingScreen
/// is a placeholder).
/// </summary>
public sealed class TestingScreen : UserControl
{
    private readonly MonitorView _monitor = new();
    private readonly Image _stimulusImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
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

        // Left: the monitor and the image stimulus share the column (one visible at a time).
        var left = new Grid();
        left.Children.Add(_monitor);
        _stimulusImage.Visibility = Visibility.Collapsed;
        left.Children.Add(_stimulusImage);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

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

    /// <summary>Mirrors the current question's stimulus onto the left pane — loading its bound ECG or
    /// image once per question (not on every answer/tick) so the visual matches what is being asked.</summary>
    private void OnTestStateChanged()
    {
        if (_monitorVm is null || _rhythmVm is null) return;

        var question = _testVm.Current;
        if (_testVm.HasActiveTest && question is not null)
        {
            if (question.Id != _loadedQuestionId)
            {
                _loadedQuestionId = question.Id;
                ApplyStimulus(question);
            }
        }
        else
        {
            _loadedQuestionId = null;
            _stimulusImage.Source = null;
            _stimulusImage.Visibility = Visibility.Collapsed;
            _monitor.Visibility = Visibility.Visible;
            _monitorVm.SetIsRunning(false);
        }
    }

    private void ApplyStimulus(TestQuestion question)
    {
        if (_monitorVm is null || _rhythmVm is null) return;

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
}
