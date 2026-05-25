using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Controls;
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
/// Editor mode: toolbar (title + Rename + Save/Revert), a lead tab strip (dirty leads in red),
/// the editable lead canvas with a looping preview strip, a right significant-point panel, and
/// the rhythm + points drawers on the left. Port of the Android <c>EditorScreen</c>. ADC values
/// are edited via the <see cref="EditorControlPanel"/> in the bottom bar.
/// </summary>
public sealed class EditorScreen : UserControl
{
    private readonly EditableLeadControl _editable = new();
    private readonly PreviewPaneControl _preview = new();
    private readonly RhythmChoosingDrawer _drawer = new();
    private readonly SignificantPointPanel _pointPanel = new();
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
    private readonly Button _renameButton = new() { Content = new SymbolIcon(Symbol.Edit), Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = "Save", Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert Lead", Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
    private readonly Grid _root = new();

    private SignificantPointsDrawer? _pointsDrawer;
    private EditorViewModel? _editorVm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private int _baseline = 1024;

    public EditorScreen()
    {
        BuildLayout();
    }

    private void BuildLayout()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Padding = new Thickness(16, 8, 16, 8),
        };
        toolbar.Children.Add(_title);
        _renameButton.Click += OnRenameClick;
        toolbar.Children.Add(_renameButton);
        _saveButton.Click += async (_, _) => { if (_editorVm is not null) await _editorVm.SaveAsync(); };
        _revertButton.Click += (_, _) => _editorVm?.RevertLead(_editorVm.FocusedLead);
        toolbar.Children.Add(_saveButton);
        toolbar.Children.Add(_revertButton);
        Grid.SetRow(toolbar, 0);
        content.Children.Add(toolbar);

        var tabScroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tabs,
        };
        Grid.SetRow(tabScroll, 1);
        content.Children.Add(tabScroll);

        // Canvas area: [editable lead + looping preview] | [significant-point panel].
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftCol = new Grid();
        leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        Grid.SetRow(_editable, 0);
        leftCol.Children.Add(_editable);

        // Looping preview strip on a light surface (Android wraps PreviewPane in a Surface).
        var previewSurface = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(new Color { A = 0xCC, R = 0xE2, G = 0xE2, B = 0xE8 }),
            Child = _preview,
        };
        Grid.SetRow(previewSurface, 1);
        leftCol.Children.Add(previewSurface);
        Grid.SetColumn(leftCol, 0);
        main.Children.Add(leftCol);

        Grid.SetColumn(_pointPanel, 1);
        main.Children.Add(_pointPanel);

        Grid.SetRow(main, 2);
        content.Children.Add(main);

        _root.Children.Add(content);
        _drawer.HorizontalAlignment = HorizontalAlignment.Left;
        _drawer.VerticalAlignment = VerticalAlignment.Center;
        _drawer.Margin = new Thickness(0, 0, 0, 120);
        _root.Children.Add(_drawer);
        Content = _root;

        _editable.IndexSelected += index => _editorVm?.SelectIndex(index);
        _pointPanel.PointToggle += (index, type) =>
        {
            if (_editorVm is not null) _editorVm.ToggleSignificantPoint(_editorVm.FocusedLead, index, type);
        };
        _drawer.RhythmSelected += (_, entry) => _editorVm?.SelectPathology(entry.Id);
    }

    public void Initialize(EditorViewModel editorVm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _editorVm = editorVm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _baseline = appVm.Repository.Manifest()?.Baseline ?? 1024;

        monitorVm.SetSeriesCount(1);
        monitorVm.SetSeriesScheme(SeriesScheme.OneColumn);

        _drawer.DisplayLanguage = appVm.SelectedLanguage;
        _drawer.SetRhythms(rhythmVm.Rhythms);
        _drawer.SelectedId = editorVm.TargetFile?.Id;

        _pointsDrawer = new SignificantPointsDrawer(editorVm, monitorVm.MonitorMode.Calibration.SampleRateHz)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 140, 0, 0),
        };
        _root.Children.Add(_pointsDrawer);

        editorVm.PropertyChanged += OnEditorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        appVm.PropertyChanged += OnAppChanged;

        UpdateCanvasAndPreview();
        UpdateToolbar();
        RefreshTabs();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _drawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _drawer.SetRhythms(_rhythmVm.Rhythms);
            UpdateCanvasAndPreview(); // localized title
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
        {
            _drawer.SetRhythms(_rhythmVm.Rhythms);
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.TargetFile):
                _drawer.SelectedId = _editorVm?.TargetFile?.Id;
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
            case nameof(EditorViewModel.FocusedLead):
            case nameof(EditorViewModel.SelectedIndex):
                UpdateCanvasAndPreview();
                RefreshTabs();
                break;
            case nameof(EditorViewModel.DirtyLeads):
            case nameof(EditorViewModel.IsMetadataDirty):
                UpdateToolbar();
                RefreshTabs();
                break;
        }
    }

    private void UpdateCanvasAndPreview()
    {
        if (_editorVm is null || _monitorVm is null || _appVm is null) return;
        var file = _editorVm.TargetFile;

        _title.Text = file is null
            ? "No pathology selected"
            : _appVm.SelectedLanguage == DomainLanguage.RU ? file.NameRu ?? file.TitleEn : file.TitleEn;

        LeadStream? stream = null;
        if (file is not null && file.Leads.TryGetValue(_editorVm.FocusedLead, out var s)) stream = s;

        var points = file?.SignificantPoints ?? Array.Empty<SignificantPoint>();
        var sampleRate = _monitorVm.MonitorMode.Calibration.SampleRateHz;

        _editable.SetData(stream, _baseline, _monitorVm.MonitorMode, points, _editorVm.SelectedIndex);
        _pointPanel.SetData(points, stream is null ? null : _editorVm.SelectedIndex, sampleRate);

        var previewValues = stream is null
            ? Array.Empty<float>()
            : stream.Samples.Select(v => (float)(v - _baseline)).ToArray();
        _preview.SetData(previewValues, _monitorVm.MonitorMode);
    }

    private void UpdateToolbar()
    {
        if (_editorVm is null) return;
        var hasChanges = _editorVm.DirtyLeads.Count > 0 || _editorVm.IsMetadataDirty;
        _saveButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        _revertButton.Visibility = _editorVm.DirtyLeads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _renameButton.Visibility = _editorVm.TargetFile != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _appVm is null) return;
        var file = _editorVm.TargetFile;
        var lang = _appVm.SelectedLanguage;
        var currentName = lang == DomainLanguage.RU ? file.NameRu ?? file.TitleEn : file.TitleEn;

        var input = new TextBox { Text = currentName, SelectionStart = currentName.Length };
        var dialog = new ContentDialog
        {
            Title = "Rename Pathology",
            Content = input,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _editorVm.Rename(input.Text, lang);
        }
    }

    private void RefreshTabs()
    {
        _tabs.Children.Clear();
        if (_editorVm is null) return;
        foreach (var lead in Leads.All)
        {
            var captured = lead;
            var isFocused = _editorVm.FocusedLead == lead;
            var isDirty = _editorVm.DirtyLeads.Contains(lead);
            var button = new Button
            {
                Content = lead.ToString(),
                Foreground = new SolidColorBrush(isDirty ? Colors.Red : Colors.Black),
                FontWeight = isFocused ? FontWeights.Bold : FontWeights.Normal,
            };
            button.Click += (_, _) => _editorVm!.SelectLead(captured);
            _tabs.Children.Add(button);
        }
    }
}
