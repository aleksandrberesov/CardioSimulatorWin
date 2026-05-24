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
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Editor mode: toolbar (title + Rename + Save/Revert), a lead tab strip (dirty leads in red),
/// the editable lead canvas, a looping preview strip, and the rhythm drawer. Port of the     
/// Android <c>EditorScreen</c>.
/// </summary>
public sealed class EditorScreen : UserControl
{
    private readonly EditableLeadControl _editable = new();
    private readonly EcgMonitorControl _preview = new();
    private readonly RhythmChoosingDrawer _drawer = new();
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
    private readonly Button _renameButton = new() { Content = new SymbolIcon(Symbol.Edit), Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = "Save", Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert Lead", Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };

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
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });       

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

        Grid.SetRow(_editable, 2);
        content.Children.Add(_editable);

        Grid.SetRow(_preview, 3);
        content.Children.Add(_preview);

        var root = new Grid();
        root.Children.Add(content);
        _drawer.HorizontalAlignment = HorizontalAlignment.Left;
        _drawer.VerticalAlignment = VerticalAlignment.Stretch;
        root.Children.Add(_drawer);
        Content = root;

        _editable.SampleChanged += (index, adc) =>
        {
            if (_editorVm is not null) _editorVm.SetSample(_editorVm.FocusedLead, index, adc);
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

        _editable.SetData(stream, _baseline, _monitorVm.MonitorMode);

        _preview.Mode = _monitorVm.MonitorMode with { Count = 1, SeriesScheme = SeriesScheme.OneColumn, IsRunning = true };
        _preview.Waveforms = stream is null
            ? new Dictionary<Lead, Points>()
            : new Dictionary<Lead, Points> { [_editorVm.FocusedLead] = new Points(stream.Samples.Select(v => (float)(v - _baseline)).ToArray()) };
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
