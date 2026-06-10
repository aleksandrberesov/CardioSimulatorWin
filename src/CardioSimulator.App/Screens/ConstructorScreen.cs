using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Constructor mode (the renamed Editor). Toolbar = title + rename + duplicate + load image +
/// delete + tool-mode switcher (when image loaded) + generate derived + save + revert. Below:
/// lead tab strip (dirty leads in red), the editable lead canvas + looping preview, a right
/// significant-point panel, the rhythm + points drawers on the left, and an image-position
/// panel overlay when in <see cref="ToolMode.Position"/>. Port of the Android
/// <c>ConstructorScreen</c>.
/// </summary>
public sealed class ConstructorScreen : UserControl
{
    private readonly EditableLeadControl _editable = new();
    private readonly PreviewPaneControl _preview = new();
    private readonly RhythmChoosingDrawer _drawer = new();
    private readonly SignificantPointPanel _pointPanel = new();
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
    private readonly Button _renameButton = new() { Content = new SymbolIcon(Symbol.Edit), Visibility = Visibility.Collapsed };
    private readonly Button _duplicateButton = new() { Content = new SymbolIcon(Symbol.Copy), Visibility = Visibility.Collapsed };
    private readonly Button _imageButton = new() { Content = new SymbolIcon(Symbol.OpenFile), Visibility = Visibility.Collapsed };
    private readonly Button _deleteButton = new() { Content = new SymbolIcon(Symbol.Delete), Visibility = Visibility.Collapsed };
    private readonly Button _calcDerivedButton = new() { Visibility = Visibility.Collapsed };
    private readonly Button _autoDetectButton = new() { Content = "Auto-detect", Visibility = Visibility.Collapsed };
    private readonly Button _applyGhostButton = new() { Content = "Apply", Visibility = Visibility.Collapsed };
    private readonly Button _cancelGhostButton = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = "Save", Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert Lead", Visibility = Visibility.Collapsed };
    private readonly StackPanel _toolSwitcher = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly RadioButton _toolSelect = new() { Content = "Select", GroupName = "ctor_tool" };
    private readonly RadioButton _toolPosition = new() { Content = "Position", GroupName = "ctor_tool" };
    private readonly RadioButton _toolTrace = new() { Content = "Trace", GroupName = "ctor_tool" };
    private readonly Button _undoButton = new() { Content = new SymbolIcon(Symbol.Undo), Visibility = Visibility.Collapsed };
    private readonly Button _redoButton = new() { Content = new SymbolIcon(Symbol.Redo), Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
    private readonly Grid _root = new();
    private Grid _contentRoot = null!;

    // Image-position floating panel (visible in Position mode).
    private readonly Border _imagePanel;
    private readonly Slider _alphaSlider = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Width = 140 };
    private readonly Slider _scaleSlider = new() { Minimum = 0.2, Maximum = 5.0, StepFrequency = 0.05, Width = 140 };
    private readonly Slider _rotationSlider = new() { Minimum = -180, Maximum = 180, StepFrequency = 1, Width = 140 };
    private readonly CheckBox _lockCheck = new() { Content = "Lock" };
    private readonly Button _resetButton = new() { Content = "Reset" };

    private SignificantPointsDrawer? _pointsDrawer;
    private ConstructorViewModel? _editorVm;
    private MonitorViewModel? _monitorVm;
    private RhythmViewModel? _rhythmVm;
    private AppViewModel? _appVm;
    private Func<Task<StorageFile?>>? _pickOpenImage;
    private int _baseline = 1024;
    private bool _suppressTransformPush;

    public ConstructorScreen()
    {
        _imagePanel = BuildImagePositionPanel();
        BuildLayout();
    }

    private Border BuildImagePositionPanel()
    {
        var stack = new StackPanel { Padding = new Thickness(12), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Image Position", FontWeight = FontWeights.SemiBold });

        var alphaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        alphaRow.Children.Add(new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        alphaRow.Children.Add(_alphaSlider);
        stack.Children.Add(alphaRow);

        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        scaleRow.Children.Add(new TextBlock { Text = "Scale", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        scaleRow.Children.Add(_scaleSlider);
        stack.Children.Add(scaleRow);

        var rotRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        rotRow.Children.Add(new TextBlock { Text = "Rotate", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        rotRow.Children.Add(_rotationSlider);
        stack.Children.Add(rotRow);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(_lockCheck);
        buttonRow.Children.Add(_resetButton);
        stack.Children.Add(buttonRow);

        return new Border
        {
            Background = new SolidColorBrush(new Color { A = 0xE0, R = 0xF5, G = 0xF5, B = 0xF8 }),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 140, 16, 0),
            Visibility = Visibility.Collapsed,
            Child = stack,
        };
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
        _duplicateButton.Click += (_, _) => _editorVm?.DuplicateCurrentPathology();
        toolbar.Children.Add(_duplicateButton);
        _imageButton.Click += OnImageClick;
        toolbar.Children.Add(_imageButton);
        _deleteButton.Click += OnDeleteClick;
        toolbar.Children.Add(_deleteButton);

        _toolSwitcher.Children.Add(_toolSelect);
        _toolSwitcher.Children.Add(_toolPosition);
        _toolSwitcher.Children.Add(_toolTrace);
        _toolSelect.Checked += (_, _) => { if (_editorVm is not null) _editorVm.ToolMode = ToolMode.Select; };
        _toolPosition.Checked += (_, _) => { if (_editorVm is not null) _editorVm.ToolMode = ToolMode.Position; };
        _toolTrace.Checked += (_, _) => { if (_editorVm is not null) _editorVm.ToolMode = ToolMode.Trace; };
        toolbar.Children.Add(_toolSwitcher);

        // Undo / redo of per-stroke sample edits (Android shows these once an image is loaded).
        _undoButton.Click += (_, _) =>
        {
            if (_editorVm is null) return;
            _editorVm.Undo(_editorVm.FocusedLead);
            UpdateCanvasAndPreview();
            UpdateToolbar();
            RefreshTabs();
        };
        _redoButton.Click += (_, _) =>
        {
            if (_editorVm is null) return;
            _editorVm.Redo(_editorVm.FocusedLead);
            UpdateCanvasAndPreview();
            UpdateToolbar();
            RefreshTabs();
        };
        toolbar.Children.Add(_undoButton);
        toolbar.Children.Add(_redoButton);

        _calcDerivedButton.Content = AppStrings.CalcDerivedLeads;
        _calcDerivedButton.Click += OnCalcDerivedClick;
        toolbar.Children.Add(_calcDerivedButton);
        _autoDetectButton.Click += OnAutoDetectClick;
        toolbar.Children.Add(_autoDetectButton);
        _applyGhostButton.Click += (_, _) => _editorVm?.ApplyGhostTrace();
        toolbar.Children.Add(_applyGhostButton);
        _cancelGhostButton.Click += (_, _) => _editorVm?.SetGhostTrace(null);
        toolbar.Children.Add(_cancelGhostButton);
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

        // Column 0 sizes to the rhythm drawer, column 1 holds the editor. Pinning the drawer
        // confines the editor to column 1 so it lays out beside the open drawer (Android's
        // isDrawerFixed branch); unpinned, the editor spans both columns with the drawer floating.
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(content, 0);
        Grid.SetColumnSpan(content, 2);
        _root.Children.Add(content);

        Grid.SetColumn(_imagePanel, 0);
        Grid.SetColumnSpan(_imagePanel, 2);
        _root.Children.Add(_imagePanel);

        _drawer.HorizontalAlignment = HorizontalAlignment.Left;
        _drawer.VerticalAlignment = VerticalAlignment.Center;
        _drawer.Margin = new Thickness(0, 0, 0, 120);
        Grid.SetColumn(_drawer, 0);
        _root.Children.Add(_drawer);
        _drawer.PinnedChanged += (_, pinned) =>
        {
            _appVm?.SetDrawerFixed(pinned);
            ApplyDrawerPin(pinned);
        };
        _contentRoot = content;
        Content = _root;

        _editable.IndexSelected += index => _editorVm?.SelectIndex(index);
        _editable.ImageOffsetChanged += (x, y) => _editorVm?.SetImageOffset(x, y);
        _editable.StrokeStarted += () => { if (_editorVm is not null) _editorVm.StartStroke(_editorVm.FocusedLead); };
        _editable.TraceUpdates += updates => { if (_editorVm is not null) _editorVm.TraceSamples(_editorVm.FocusedLead, updates); };
        _pointPanel.PointToggle += (index, type) =>
        {
            if (_editorVm is not null) _editorVm.ToggleSignificantPoint(_editorVm.FocusedLead, index, type);
        };
        _drawer.RhythmSelected += (_, entry) => _editorVm?.SelectPathology(entry.Id);

        _alphaSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageAlpha((float)_alphaSlider.Value); };
        _scaleSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageScale((float)_scaleSlider.Value); };
        _rotationSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageRotation((float)_rotationSlider.Value); };
        _lockCheck.Checked += (_, _) => _editorVm?.SetImageLocked(true);
        _lockCheck.Unchecked += (_, _) => _editorVm?.SetImageLocked(false);
        _resetButton.Click += (_, _) => _editorVm?.ResetImageTransform();
    }

    /// <summary>Pinned: drawer stays open and the editor is confined to column 1 (lays out beside
    /// it); unpinned: the editor spans both columns and the drawer floats over the left edge.</summary>
    private void ApplyDrawerPin(bool pinned)
    {
        _drawer.SetPinned(pinned);
        _drawer.VerticalAlignment = pinned ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        _drawer.Margin = pinned ? new Thickness(0) : new Thickness(0, 0, 0, 120);
        if (pinned)
        {
            Grid.SetColumn(_contentRoot, 1);
            Grid.SetColumnSpan(_contentRoot, 1);
        }
        else
        {
            Grid.SetColumn(_contentRoot, 0);
            Grid.SetColumnSpan(_contentRoot, 2);
        }
    }

    public void Initialize(
        ConstructorViewModel editorVm,
        MonitorViewModel monitorVm,
        RhythmViewModel rhythmVm,
        AppViewModel appVm,
        Func<Task<StorageFile?>>? pickOpenImage = null)
    {
        _editorVm = editorVm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _pickOpenImage = pickOpenImage;
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
        Grid.SetColumn(_pointsDrawer, 0);
        _root.Children.Add(_pointsDrawer);

        ApplyDrawerPin(appVm.IsDrawerFixed);

        editorVm.PropertyChanged += OnEditorChanged;
        rhythmVm.PropertyChanged += OnRhythmChanged;
        appVm.PropertyChanged += OnAppChanged;

        SyncToolSwitcher();
        SyncImagePanelSliders();
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
            UpdateCanvasAndPreview();
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

    private async void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConstructorViewModel.TargetFile):
                _drawer.SelectedId = _editorVm?.TargetFile?.Id;
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
            case nameof(ConstructorViewModel.FocusedLead):
            case nameof(ConstructorViewModel.SelectedIndex):
                UpdateCanvasAndPreview();
                RefreshTabs();
                UpdateToolbar();
                break;
            case nameof(ConstructorViewModel.DirtyLeads):
            case nameof(ConstructorViewModel.IsMetadataDirty):
                UpdateToolbar();
                RefreshTabs();
                break;
            case nameof(ConstructorViewModel.ImageTransform):
                SyncImagePanelSliders();
                UpdateCanvasAndPreview();
                break;
            case nameof(ConstructorViewModel.ToolMode):
                SyncToolSwitcher();
                _imagePanel.Visibility = (_editorVm?.ToolMode == ToolMode.Position && _editorVm.ReferenceImageUri is not null)
                    ? Visibility.Visible : Visibility.Collapsed;
                UpdateCanvasAndPreview();
                break;
            case nameof(ConstructorViewModel.ReferenceImageUri):
                if (_editorVm is not null)
                {
                    await _editable.SetReferenceImageAsync(_editorVm.ReferenceImageUri);
                }
                _toolSwitcher.Visibility = _editorVm?.ReferenceImageUri is not null ? Visibility.Visible : Visibility.Collapsed;
                _imagePanel.Visibility = (_editorVm?.ToolMode == ToolMode.Position && _editorVm.ReferenceImageUri is not null)
                    ? Visibility.Visible : Visibility.Collapsed;
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
            case nameof(ConstructorViewModel.GhostTrace):
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
        }
    }

    private void SyncToolSwitcher()
    {
        if (_editorVm is null) return;
        _suppressTransformPush = true;
        try
        {
            _toolSelect.IsChecked = _editorVm.ToolMode == ToolMode.Select;
            _toolPosition.IsChecked = _editorVm.ToolMode == ToolMode.Position;
            _toolTrace.IsChecked = _editorVm.ToolMode == ToolMode.Trace;
        }
        finally { _suppressTransformPush = false; }
    }

    private void SyncImagePanelSliders()
    {
        if (_editorVm is null) return;
        var t = _editorVm.ImageTransform;
        _suppressTransformPush = true;
        try
        {
            _alphaSlider.Value = t.Alpha;
            _scaleSlider.Value = t.Scale;
            _rotationSlider.Value = t.RotationDeg;
            _lockCheck.IsChecked = t.IsLocked;
            _resetButton.IsEnabled = !t.IsLocked;
            _scaleSlider.IsEnabled = !t.IsLocked;
            _rotationSlider.IsEnabled = !t.IsLocked;
        }
        finally { _suppressTransformPush = false; }
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

        _editable.SetData(stream, _baseline, _monitorVm.MonitorMode, points, _editorVm.SelectedIndex,
            _editorVm.ImageTransform, _editorVm.ToolMode, _editorVm.GhostTrace);
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
        var hasTarget = _editorVm.TargetFile != null;
        _saveButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        _revertButton.Visibility = _editorVm.DirtyLeads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _renameButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _duplicateButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _imageButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _calcDerivedButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;

        var hasImage = _editorVm.ReferenceImageUri is not null;
        var hasGhost = _editorVm.GhostTrace is not null;

        _undoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _redoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _undoButton.IsEnabled = _editorVm.CanUndo(_editorVm.FocusedLead);
        _redoButton.IsEnabled = _editorVm.CanRedo(_editorVm.FocusedLead);

        _autoDetectButton.Visibility = (hasTarget && hasImage && _editorVm.ToolMode == ToolMode.Trace && !hasGhost) ? Visibility.Visible : Visibility.Collapsed;
        _applyGhostButton.Visibility = hasGhost ? Visibility.Visible : Visibility.Collapsed;
        _cancelGhostButton.Visibility = hasGhost ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutoDetectClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null || _monitorVm is null) return;
        var bitmap = _editable.ReferenceImage;
        if (bitmap is null) return;
        var file = _editorVm.TargetFile;
        if (file is null || !file.Leads.TryGetValue(_editorVm.FocusedLead, out var stream)) return;
        var mode = _monitorVm.MonitorMode;
        var scale = new PixelScale(EcgRenderer.PxPerMm(mode.DisplayScale), mode.Speed, 1f, mode.Calibration);
        var trace = TraceExtractor.Extract(
            bitmap, stream.Samples.Length, _baseline,
            scale.PxPerSample, scale.PxPerAdcCount,
            _editorVm.ImageTransform,
            (float)_editable.ActualWidth, (float)_editable.ActualHeight);
        if (trace is not null) _editorVm.SetGhostTrace(trace);
    }

    private async void OnImageClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null || _pickOpenImage is null) return;
        var file = await _pickOpenImage();
        if (file is null) return;
        _editorVm.SetReferenceImageUri(file.Path);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null) return;
        var dialog = new ContentDialog
        {
            Title = "Delete pathology?",
            Content = "This permanently removes the pathology file and its manifest entry. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _editorVm.DeleteCurrentPathology();
        }
    }

    private async void OnCalcDerivedClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null) return;
        var body = new TextBlock
        {
            Text =
                "Calculate the derived leads from I + II and V2 + V6? Existing samples in the derived leads " +
                "(III, aVR, aVL, aVF, V1, V3, V4, V5) will be overwritten.\n\n" +
                "Formulas:\n" +
                "  III = II - I\n" +
                "  aVR = -(I + II) / 2\n" +
                "  aVL = (2·I - II) / 2\n" +
                "  aVF = (2·II - I) / 2\n" +
                "  V1/V3/V4/V5: angular projection from V2 (94°) and V6 (0°)",
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            Title = "Generate derived leads",
            Content = body,
            PrimaryButtonText = "Generate",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _editorVm.CalculateDerivedLeads();
        }
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
