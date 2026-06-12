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
/// Constructor mode. Toolbar = title + rename + duplicate + delete + generate derived +
/// undo/redo (when image loaded) + save + revert. Below: lead tab strip (dirty leads in red),
/// the editable lead canvas + looping preview, a mode-specific right panel, and the vertical
/// ToolModePanel sidebar. Port of the Android <c>ConstructorScreen</c>.
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
    private readonly Button _deleteButton = new() { Content = new SymbolIcon(Symbol.Delete), Visibility = Visibility.Collapsed };
    private readonly Button _calcDerivedButton = new() { Visibility = Visibility.Collapsed };
    private readonly Button _undoButton = new() { Content = new SymbolIcon(Symbol.Undo), Visibility = Visibility.Collapsed };
    private readonly Button _redoButton = new() { Content = new SymbolIcon(Symbol.Redo), Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = "Save", Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert Lead", Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
    private readonly Grid _root = new();
    private Grid _contentRoot = null!;

    // ── ToolModePanel sidebar (rightmost column, 56 px) ────────────────────
    private readonly ToolModePanelControl _toolModePanel = new();

    // ── Mode-specific panel host (swapped on ToolMode change) ─────────────
    private readonly Border _modePanelHost = new() { Width = 240, VerticalAlignment = VerticalAlignment.Stretch };

    // Draw (Trace) mode panel controls
    private readonly Button _drawAutoDetectBtn = new() { Content = "Auto-detect", Visibility = Visibility.Collapsed };
    private readonly Button _drawUndoBtn = new() { Content = new SymbolIcon(Symbol.Undo) };
    private readonly Border _ghostAcceptArea = new() { Visibility = Visibility.Collapsed };
    private readonly Button _applyGhostBtn = new() { Content = "Apply" };
    private readonly Button _cancelGhostBtn = new() { Content = "Cancel" };

    // Photo mode panel controls
    private readonly Button _photoLoadBtn = new() { Content = new SymbolIcon(Symbol.OpenFile) };
    private readonly CheckBox _photoVisibleCheck = new() { Content = "Visible" };
    private readonly CheckBox _photoLockCheck = new() { Content = "Lock" };
    private readonly Button _photoResetBtn = new() { Content = "Reset" };
    private readonly Button _photoDeleteBtn = new() { Content = new SymbolIcon(Symbol.Delete) };
    private readonly Slider _alphaSlider = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Width = 200 };
    private readonly Slider _scaleSlider = new() { Minimum = 0.2, Maximum = 5.0, StepFrequency = 0.05, Width = 200 };
    private readonly Slider _rotationSlider = new() { Minimum = -180, Maximum = 180, StepFrequency = 1, Width = 200 };
    private readonly StackPanel _photoSlidersArea = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly TextBlock _photoNoImageLabel = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) };

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
        BuildLayout();
    }

    private void BuildLayout()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── Toolbar ─────────────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(16, 8, 16, 8),
        };
        toolbar.Children.Add(_title);
        _renameButton.Click += OnRenameClick;
        toolbar.Children.Add(_renameButton);
        _duplicateButton.Click += (_, _) => _editorVm?.DuplicateCurrentPathology();
        toolbar.Children.Add(_duplicateButton);
        _deleteButton.Click += OnDeleteClick;
        toolbar.Children.Add(_deleteButton);
        _calcDerivedButton.Content = AppStrings.CalcDerivedLeads;
        _calcDerivedButton.Click += OnCalcDerivedClick;
        toolbar.Children.Add(_calcDerivedButton);

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

        _saveButton.Click += async (_, _) => { if (_editorVm is not null) await _editorVm.SaveAsync(); };
        _revertButton.Click += (_, _) => _editorVm?.RevertLead(_editorVm.FocusedLead);
        toolbar.Children.Add(_saveButton);
        toolbar.Children.Add(_revertButton);
        Grid.SetRow(toolbar, 0);
        content.Children.Add(toolbar);

        // ── Lead tabs ────────────────────────────────────────────────────────
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

        // ── Canvas area: [editable lead + preview] | [mode panel] | [tool mode icons] ─
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

        // Build all mode-specific panels, default to Select.
        BuildModePanels();
        _modePanelHost.Child = BuildSelectPanel();
        Grid.SetColumn(_modePanelHost, 1);
        main.Children.Add(_modePanelHost);

        Grid.SetColumn(_toolModePanel, 2);
        main.Children.Add(_toolModePanel);

        Grid.SetRow(main, 2);
        content.Children.Add(main);

        // ── Root layout (drawer | content) ──────────────────────────────────
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 0);
        Grid.SetColumnSpan(content, 2);
        _root.Children.Add(content);

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

        // ── Event wiring ─────────────────────────────────────────────────────
        _editable.IndexSelected += index => _editorVm?.SelectIndex(index);
        _editable.ImageOffsetChanged += (x, y) => _editorVm?.SetImageOffset(x, y);
        _editable.StrokeStarted += () => { if (_editorVm is not null) _editorVm.StartStroke(_editorVm.FocusedLead); };
        _editable.TraceUpdates += updates => { if (_editorVm is not null) _editorVm.TraceSamples(_editorVm.FocusedLead, updates); };
        _pointPanel.PointToggle += (index, type) =>
        {
            if (_editorVm is not null) _editorVm.ToggleSignificantPoint(_editorVm.FocusedLead, index, type);
        };
        _drawer.RhythmSelected += (_, entry) => _editorVm?.SelectPathology(entry.Id);
        _toolModePanel.ModeChanged += mode => { if (_editorVm is not null) _editorVm.ToolMode = mode; };

        // Draw panel
        _drawAutoDetectBtn.Click += OnAutoDetectClick;
        _drawUndoBtn.Click += (_, _) =>
        {
            if (_editorVm is null) return;
            _editorVm.Undo(_editorVm.FocusedLead);
            UpdateCanvasAndPreview();
            UpdateToolbar();
            RefreshTabs();
        };
        _applyGhostBtn.Click += (_, _) => _editorVm?.ApplyGhostTrace();
        _cancelGhostBtn.Click += (_, _) => _editorVm?.SetGhostTrace(null);

        // Photo panel
        _photoLoadBtn.Click += OnImageClick;
        _photoDeleteBtn.Click += (_, _) => _editorVm?.SetReferenceImageUri(null);
        _photoResetBtn.Click += (_, _) => _editorVm?.ResetImageTransform();
        _photoVisibleCheck.Checked += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageVisible(true); };
        _photoVisibleCheck.Unchecked += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageVisible(false); };
        _photoLockCheck.Checked += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageLocked(true); };
        _photoLockCheck.Unchecked += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageLocked(false); };
        _alphaSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageAlpha((float)_alphaSlider.Value); };
        _scaleSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageScale((float)_scaleSlider.Value); };
        _rotationSlider.ValueChanged += (_, _) => { if (!_suppressTransformPush) _editorVm?.SetImageRotation((float)_rotationSlider.Value); };
    }

    // ── Mode panel builders ─────────────────────────────────────────────────

    private void BuildModePanels()
    {
        // Wire ghost-accept area content (shared across calls to BuildDrawPanel).
        var ghostInner = new StackPanel { Spacing = 4, Padding = new Thickness(8) };
        ghostInner.Children.Add(new TextBlock { Text = "Apply auto-detected trace?", TextWrapping = TextWrapping.Wrap });
        var ghostBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        ghostBtns.Children.Add(_applyGhostBtn);
        ghostBtns.Children.Add(_cancelGhostBtn);
        ghostInner.Children.Add(ghostBtns);
        _ghostAcceptArea.CornerRadius = new CornerRadius(6);
        _ghostAcceptArea.Background = new SolidColorBrush(new Color { A = 0xFF, R = 0xCB, G = 0xE5, B = 0xCC });
        _ghostAcceptArea.Child = ghostInner;

        // Wire photo sliders area.
        _photoSlidersArea.Children.Add(LabeledSlider("Opacity", _alphaSlider));
        _photoSlidersArea.Children.Add(LabeledSlider("Scale", _scaleSlider));
        _photoSlidersArea.Children.Add(LabeledSlider("Rotation", _rotationSlider));
        _photoNoImageLabel.Text = "Load a reference image to enable tracing.";
    }

    private static UIElement LabeledSlider(string label, Slider slider)
    {
        var col = new StackPanel { Spacing = 2 };
        col.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
        col.Children.Add(slider);
        return col;
    }

    private static Border MakePanelBorder(UIElement child)
        => new()
        {
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(new Color { A = 0x80, R = 0xE8, G = 0xE8, B = 0xF0 }),
            Child = child,
        };

    private static Border Divider()
        => new() { Height = 1, Background = new SolidColorBrush(new Color { A = 0x40, R = 0x80, G = 0x80, B = 0x80 }), Margin = new Thickness(0, 4, 0, 4) };

    private UIElement BuildSelectPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = "Select", FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        return MakePanelBorder(col);
    }

    private UIElement BuildPositionPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = "Position", FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        col.Children.Add(new TextBlock { Text = "Drag the image on the canvas to reposition it.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        return MakePanelBorder(col);
    }

    private UIElement BuildDrawPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = "Trace", FontWeight = FontWeights.SemiBold, Opacity = 0.7 });

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actionRow.Children.Add(_drawAutoDetectBtn);
        actionRow.Children.Add(_drawUndoBtn);
        col.Children.Add(actionRow);

        col.Children.Add(Divider());
        col.Children.Add(_ghostAcceptArea);
        return MakePanelBorder(col);
    }

    private UIElement BuildPointsPanel() => _pointPanel;

    private UIElement BuildPhotoPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = "Image", FontWeight = FontWeights.SemiBold, Opacity = 0.7 });

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        ToolTipService.SetToolTip(_photoLoadBtn, "Load reference image");
        ToolTipService.SetToolTip(_photoDeleteBtn, "Remove reference image");
        ToolTipService.SetToolTip(_photoResetBtn, "Reset transform");
        actionRow.Children.Add(_photoLoadBtn);
        actionRow.Children.Add(_photoVisibleCheck);
        actionRow.Children.Add(_photoLockCheck);
        actionRow.Children.Add(_photoResetBtn);
        actionRow.Children.Add(_photoDeleteBtn);
        col.Children.Add(actionRow);

        col.Children.Add(Divider());
        col.Children.Add(_photoSlidersArea);
        col.Children.Add(_photoNoImageLabel);
        return MakePanelBorder(col);
    }

    private void SwitchToModePanel(ToolMode mode)
    {
        _modePanelHost.Child = mode switch
        {
            ToolMode.Select   => BuildSelectPanel(),
            ToolMode.Trace    => BuildDrawPanel(),
            ToolMode.Position => BuildPositionPanel(),
            ToolMode.Points   => BuildPointsPanel(),
            ToolMode.Photo    => BuildPhotoPanel(),
            _                 => BuildSelectPanel(),
        };
        _toolModePanel.SetMode(mode);
    }

    // ── Drawer pin ──────────────────────────────────────────────────────────

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

    // ── Initialize ──────────────────────────────────────────────────────────

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
        monitorVm.PropertyChanged += OnMonitorChanged;

        SwitchToModePanel(editorVm.ToolMode);
        SyncPhotoPanel();
        SyncDrawPanel();
        UpdateCanvasAndPreview();
        UpdateToolbar();
        RefreshTabs();
    }

    // ── Property change handlers ────────────────────────────────────────────

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _drawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null) _drawer.SetRhythms(_rhythmVm.Rhythms);
            UpdateCanvasAndPreview();
        }
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode))
            UpdateCanvasAndPreview();
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
            _drawer.SetRhythms(_rhythmVm.Rhythms);
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
                SyncPhotoPanel();
                UpdateCanvasAndPreview();
                break;
            case nameof(ConstructorViewModel.ToolMode):
                if (_editorVm is not null) SwitchToModePanel(_editorVm.ToolMode);
                SyncDrawPanel();
                SyncPhotoPanel();
                UpdateCanvasAndPreview();
                break;
            case nameof(ConstructorViewModel.ReferenceImageUri):
                if (_editorVm is not null)
                    await _editable.SetReferenceImageAsync(_editorVm.ReferenceImageUri);
                SyncPhotoPanel();
                SyncDrawPanel();
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
            case nameof(ConstructorViewModel.GhostTrace):
                SyncDrawPanel();
                UpdateCanvasAndPreview();
                UpdateToolbar();
                break;
        }
    }

    // ── Panel sync ──────────────────────────────────────────────────────────

    private void SyncDrawPanel()
    {
        if (_editorVm is null) return;
        var hasImage = _editorVm.ReferenceImageUri is not null;
        var hasGhost = _editorVm.GhostTrace is not null;

        _drawAutoDetectBtn.IsEnabled = hasImage && !hasGhost;
        _drawAutoDetectBtn.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _drawUndoBtn.IsEnabled = _editorVm.CanUndo(_editorVm.FocusedLead);
        _ghostAcceptArea.Visibility = hasGhost ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncPhotoPanel()
    {
        if (_editorVm is null) return;
        var t = _editorVm.ImageTransform;
        var hasImage = _editorVm.ReferenceImageUri is not null;

        _suppressTransformPush = true;
        try
        {
            _alphaSlider.Value = t.Alpha;
            _scaleSlider.Value = t.Scale;
            _rotationSlider.Value = t.RotationDeg;
            _photoVisibleCheck.IsChecked = t.IsVisible;
            _photoLockCheck.IsChecked = t.IsLocked;
            _scaleSlider.IsEnabled = !t.IsLocked;
            _rotationSlider.IsEnabled = !t.IsLocked;
            _photoResetBtn.IsEnabled = !t.IsLocked && hasImage;
            _photoDeleteBtn.IsEnabled = hasImage;
        }
        finally { _suppressTransformPush = false; }

        _photoSlidersArea.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _photoNoImageLabel.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Canvas / preview ────────────────────────────────────────────────────

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

    // ── Toolbar state ───────────────────────────────────────────────────────

    private void UpdateToolbar()
    {
        if (_editorVm is null) return;
        var hasChanges = _editorVm.DirtyLeads.Count > 0 || _editorVm.IsMetadataDirty;
        var hasTarget = _editorVm.TargetFile != null;
        var hasImage = _editorVm.ReferenceImageUri is not null;

        _saveButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        _revertButton.Visibility = _editorVm.DirtyLeads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _renameButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _duplicateButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _calcDerivedButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;

        _undoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _redoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _undoButton.IsEnabled = _editorVm.CanUndo(_editorVm.FocusedLead);
        _redoButton.IsEnabled = _editorVm.CanRedo(_editorVm.FocusedLead);
    }

    // ── Dialog handlers ─────────────────────────────────────────────────────

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

    // ── Tabs ────────────────────────────────────────────────────────────────

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
