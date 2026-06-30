using System.ComponentModel;
using System.IO;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Data.Wfdb;
using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Network;
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
    private readonly Button _newButton = new() { Content = new SymbolIcon(Symbol.Add) };
    private readonly Button _importButton = new() { Content = new SymbolIcon(Symbol.Import) };
    private readonly Button _renameButton = new() { Content = new SymbolIcon(Symbol.Edit), Visibility = Visibility.Collapsed };
    private readonly Button _groupButton = new() { Content = new SymbolIcon(Symbol.Tag), Visibility = Visibility.Collapsed };
    private readonly Button _duplicateButton = new() { Content = new SymbolIcon(Symbol.Copy), Visibility = Visibility.Collapsed };
    private readonly Button _deleteButton = new() { Content = new SymbolIcon(Symbol.Delete), Visibility = Visibility.Collapsed };
    private readonly Button _calcDerivedButton = new() { Content = new SymbolIcon(Symbol.Calculator), Visibility = Visibility.Collapsed };
    private readonly Button _insertElementButton = new() { Content = new SymbolIcon(Symbol.AllApps), Visibility = Visibility.Collapsed };
    private readonly Button _manageElementsButton = new() { Content = new SymbolIcon(Symbol.List), Visibility = Visibility.Collapsed };
    private readonly Button _undoButton = new() { Content = new SymbolIcon(Symbol.Undo), Visibility = Visibility.Collapsed };
    private readonly Button _redoButton = new() { Content = new SymbolIcon(Symbol.Redo), Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = new SymbolIcon(Symbol.Save), Visibility = Visibility.Collapsed };
    private readonly Button _synthButton = new() { Content = new SymbolIcon(Symbol.Audio), Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert Lead", Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
    private readonly StackPanel _palette = new() { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(16, 2, 16, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Button> _paletteButtons = new();
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
    private Func<Task<StorageFile?>>? _pickOpenWfdb;
    private int _baseline = 1024;
    private bool _suppressTransformPush;
    private string? _lastTargetId;
    private string? _lastTargetTitleEn;
    private string? _lastTargetNameRu;
    private string? _lastTargetGroup;

    public ConstructorScreen()
    {
        BuildLayout();
    }

    private void BuildLayout()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // toolbar
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // lead tabs
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // element palette
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // canvas

        // ── Toolbar ─────────────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(16, 8, 16, 8),
        };
        toolbar.Children.Add(_title);
        _newButton.Click += OnNewClick;
        toolbar.Children.Add(_newButton);

        var importMenu = new MenuFlyout();
        var importFileItem = new MenuFlyoutItem { Text = "Import WFDB file…", Icon = new SymbolIcon(Symbol.OpenFile) };
        importFileItem.Click += OnImportWfdbFileClick;
        var importNetItem = new MenuFlyoutItem { Text = "Download from PhysioNet…", Icon = new SymbolIcon(Symbol.Download) };
        importNetItem.Click += OnImportPhysioNetClick;
        importMenu.Items.Add(importFileItem);
        importMenu.Items.Add(importNetItem);
        _importButton.Flyout = importMenu;
        ToolTipService.SetToolTip(_importButton, "Import an ECG record (WFDB file or PhysioNet)");
        toolbar.Children.Add(_importButton);

        _renameButton.Click += OnRenameClick;
        toolbar.Children.Add(_renameButton);
        _groupButton.Click += OnGroupClick;
        ToolTipService.SetToolTip(_groupButton, AppStrings.GroupEditTitle);
        toolbar.Children.Add(_groupButton);
        _duplicateButton.Click += OnDuplicateClick;
        toolbar.Children.Add(_duplicateButton);
        _deleteButton.Click += OnDeleteClick;
        toolbar.Children.Add(_deleteButton);
        ToolTipService.SetToolTip(_calcDerivedButton, AppStrings.CalcDerivedLeads);
        _calcDerivedButton.Click += OnCalcDerivedClick;
        toolbar.Children.Add(_calcDerivedButton);

        ToolTipService.SetToolTip(_insertElementButton, "Insert element");
        _insertElementButton.Click += OnInsertElementClick;
        toolbar.Children.Add(_insertElementButton);

        ToolTipService.SetToolTip(_manageElementsButton, "Manage elements");
        _manageElementsButton.Click += OnManageElementsClick;
        toolbar.Children.Add(_manageElementsButton);

        ToolTipService.SetToolTip(_synthButton, "Dolinský Synthesizer");
        _synthButton.Click += OnSynthClick;
        toolbar.Children.Add(_synthButton);

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

        ToolTipService.SetToolTip(_saveButton, "Save");
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

        // ── Element palette (one-click "library of artifacts" insert at the cursor) ──
        BuildPalette();
        Grid.SetRow(_palette, 2);
        content.Children.Add(_palette);

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

        Grid.SetRow(main, 3);
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
        _pointPanel.AutoDetectClick += OnAutoDetectPoints;
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

    private UIElement BuildPanPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = "Pan", FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        col.Children.Add(new TextBlock
        {
            Text = "Drag the trace to move the view. Scroll the mouse wheel to zoom (1–5×). " +
                   "Right-drag pans in any tool.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0),
        });
        var resetBtn = new Button { Content = "Reset view", Margin = new Thickness(0, 8, 0, 0) };
        resetBtn.Click += (_, _) => _editable.ResetView();
        col.Children.Add(resetBtn);
        return MakePanelBorder(col);
    }

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
            ToolMode.Pan      => BuildPanPanel(),
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
        Func<Task<StorageFile?>>? pickOpenImage = null,
        Func<Task<StorageFile?>>? pickOpenWfdb = null)
    {
        _editorVm = editorVm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _pickOpenImage = pickOpenImage;
        _pickOpenWfdb = pickOpenWfdb;
        _baseline = appVm.Repository.Manifest()?.Baseline ?? 1024;

        monitorVm.SetSeriesCount(1);
        monitorVm.SetSeriesScheme(SeriesScheme.OneColumn);

        _drawer.DisplayLanguage = appVm.SelectedLanguage;
        _drawer.SetRhythms(rhythmVm.Rhythms);
        _drawer.SelectedId = editorVm.TargetFile?.Id;
        _lastTargetId = editorVm.TargetFile?.Id;
        _lastTargetTitleEn = editorVm.TargetFile?.TitleEn;
        _lastTargetNameRu = editorVm.TargetFile?.NameRu;
        _lastTargetGroup = editorVm.TargetFile?.Group;

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
        {
            _drawer.SetRhythms(_rhythmVm.Rhythms);
            RefreshRhythmListNames();
        }
    }

    /// <summary>
    /// Patches the drawer's rhythm list so the in-memory (unsaved) name and group of the currently
    /// edited pathology are reflected immediately — a rename re-labels the row, and a group change
    /// moves it to its new section — before the file is saved.
    /// </summary>
    private void RefreshRhythmListNames()
    {
        var file = _editorVm?.TargetFile;
        if (file is null || _rhythmVm is null) return;
        var patched = _rhythmVm.Rhythms
            .Select(e => e.Id == file.Id
                ? e with { TitleEn = file.TitleEn, NameRu = file.NameRu, Group = file.Group }
                : e)
            .ToList();
        _drawer.SetRhythms(patched);
    }

    private async void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConstructorViewModel.TargetFile):
                var tf = _editorVm?.TargetFile;
                _drawer.SelectedId = tf?.Id;
                if (tf?.Id != _lastTargetId || tf?.TitleEn != _lastTargetTitleEn
                    || tf?.NameRu != _lastTargetNameRu || tf?.Group != _lastTargetGroup)
                {
                    _lastTargetId = tf?.Id;
                    _lastTargetTitleEn = tf?.TitleEn;
                    _lastTargetNameRu = tf?.NameRu;
                    _lastTargetGroup = tf?.Group;
                    RefreshRhythmListNames();
                }
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
        _groupButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _duplicateButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _calcDerivedButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _insertElementButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _manageElementsButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _synthButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;

        _undoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _redoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _undoButton.IsEnabled = _editorVm.CanUndo(_editorVm.FocusedLead);
        _redoButton.IsEnabled = _editorVm.CanRedo(_editorVm.FocusedLead);

        RefreshPalette();
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
            scale.PxPerSample, scale.PxPerAdcCount, EcgRenderer.TraceLeft(scale),
            _editorVm.ImageTransform,
            (float)_editable.ActualWidth, (float)_editable.ActualHeight);
        if (trace is not null) _editorVm.SetGhostTrace(trace);
    }

    private async void OnSynthClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;
        var lead = _editorVm.FocusedLead;
        if (!ConstructorViewModel.IsLeadEditable(lead))
        {
            var warning = new ContentDialog
            {
                Title = "Error",
                Content = "This lead is read-only (derived from lead I and II or V2 and V6). Select another lead to edit.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await warning.ShowAsync();
            return;
        }

        var hrSlider = new Slider { Header = "Heart Rate (BPM)", Minimum = 45, Maximum = 160, Value = 75, StepFrequency = 5 };
        var apSlider = new Slider { Header = "P-wave Amplitude (Ap) [mV]", Minimum = -0.2, Maximum = 0.5, Value = 0.2, StepFrequency = 0.05 };
        var kpSlider = new Slider { Header = "P-wave Duration (Kp)", Minimum = 10, Maximum = 100, Value = 80, StepFrequency = 5 };
        var arSlider = new Slider { Header = "R-wave Amplitude (Ar) [mV]", Minimum = 0.5, Maximum = 2.0, Value = 1.0, StepFrequency = 0.1 };
        var krSlider = new Slider { Header = "R-wave Duration (Kr)", Minimum = 10, Maximum = 150, Value = 40, StepFrequency = 5 };
        var asSlider = new Slider { Header = "S-wave Amplitude (As) [mV]", Minimum = 0.0, Maximum = 1.0, Value = 0.2, StepFrequency = 0.05 };
        var ksSlider = new Slider { Header = "S-wave Duration (Ks)", Minimum = 10, Maximum = 200, Value = 30, StepFrequency = 5 };
        var atSlider = new Slider { Header = "T-wave Amplitude (At) [mV]", Minimum = -0.5, Maximum = 1.0, Value = 0.15, StepFrequency = 0.05 };
        var ktSlider = new Slider { Header = "T-wave Duration (Kt)", Minimum = 50, Maximum = 300, Value = 220, StepFrequency = 10 };
        var varSlider = new Slider { Header = "Beat-to-Beat Variability", Minimum = 0.0, Maximum = 0.15, Value = 0.01, StepFrequency = 0.01 };

        var panel = new StackPanel { Spacing = 8, Width = 300 };
        panel.Children.Add(hrSlider);
        panel.Children.Add(apSlider);
        panel.Children.Add(kpSlider);
        panel.Children.Add(arSlider);
        panel.Children.Add(krSlider);
        panel.Children.Add(asSlider);
        panel.Children.Add(ksSlider);
        panel.Children.Add(atSlider);
        panel.Children.Add(ktSlider);
        panel.Children.Add(varSlider);

        var dialog = new ContentDialog
        {
            Title = "Dolinský Analytical ECG Synthesizer",
            Content = new ScrollViewer { Content = panel, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Generate",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            double fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
            double rrMs = 60000.0 / hrSlider.Value;
            
            int kpVal = (int)kpSlider.Value;
            int krVal = (int)krSlider.Value;
            int ksVal = (int)ksSlider.Value;
            int ktVal = (int)ktSlider.Value;
            
            int kbVal = 130;
            int kpqVal = 40;
            int kq1Val = 25;
            int kq2Val = 5;
            int kcsVal = 5;
            int kstVal = 100;
            
            int fixedSum = kbVal + kpVal + kpqVal + kq1Val + kq2Val + krVal + ksVal - kcsVal + kstVal + ktVal;
            int targetTotalSamples = (int)Math.Round(rrMs / 1000.0 * fs);
            
            int kiVal = targetTotalSamples - fixedSum;
            if (kiVal < 10) kiVal = 50;

            var result = BioSPPy.Net.Synthesizers.Ecg.DolinskySynthesizer.Generate(
                Kb: kbVal, Ap: apSlider.Value, Kp: kpVal, Kpq: kpqVal,
                Aq: 0.1, Kq1: kq1Val, Kq2: kq2Val,
                Ar: arSlider.Value, Kr: krVal,
                As: asSlider.Value, Ks: ksVal, Kcs: kcsVal,
                sm: 96, Kst: kstVal,
                At: atSlider.Value, Kt: ktVal,
                si: 2, Ki: kiVal,
                var: varSlider.Value,
                samplingRate: fs
            );

            var cal = _monitorVm.MonitorMode.Calibration;
            double mvToAdc = cal.AdcCountsPerMv;

            int[] adcSamples = new int[result.ecg.Length];
            for (int idx = 0; idx < adcSamples.Length; idx++)
            {
                double adcValue = _baseline + result.ecg[idx] * mvToAdc;
                adcSamples[idx] = Math.Clamp((int)Math.Round(adcValue), 0, 2048);
            }

            var currentFile = _editorVm.TargetFile;
            if (currentFile is not null && currentFile.Leads.TryGetValue(lead, out var stream))
            {
                int targetLen = stream.Samples.Length;
                int[] finalSamples = new int[targetLen];
                for (int idx = 0; idx < targetLen; idx++)
                {
                    finalSamples[idx] = adcSamples[idx % adcSamples.Length];
                }
                
                _editorVm.SetSampleRange(lead, 0, finalSamples);
                
                UpdateCanvasAndPreview();
                UpdateToolbar();
                RefreshTabs();
            }
        }
        catch (Exception ex)
        {
            var errDialog = new ContentDialog
            {
                Title = "Synthesis Error",
                Content = $"Failed to generate waveform: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await errDialog.ShowAsync();
        }
    }

    private async void OnAutoDetectPoints()
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;
        var lead = _editorVm.FocusedLead;
        var file = _editorVm.TargetFile;
        if (!file.Leads.TryGetValue(lead, out var stream)) return;

        try
        {
            double fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
            double[] sigDouble = stream.Samples.Select(x => (double)(x - _baseline)).ToArray();

            int[] rpeaks = BioSPPy.Net.Signals.Ecg.QrsSegmenters.HamiltonSegmenter(sigDouble, fs);
            rpeaks = BioSPPy.Net.Signals.Ecg.QrsSegmenters.CorrectRPeaks(sigDouble, rpeaks, fs, 0.05);

            if (rpeaks.Length == 0)
            {
                var noPeaks = new ContentDialog
                {
                    Title = "Auto-Detect",
                    Content = "No R-peaks detected. Ensure the signal is valid and has visible QRS complexes.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot,
                };
                await noPeaks.ShowAsync();
                return;
            }

            var landmarks = BioSPPy.Net.Signals.Ecg.FiducialPoints.GetLandmarks(sigDouble, rpeaks, fs);

            var sigPoints = new List<SignificantPoint>();
            foreach (var r in rpeaks)
            {
                sigPoints.Add(new SignificantPoint(r, EcgPointType.R_PEAK));
            }
            foreach (var lm in landmarks)
            {
                if (lm.QPeak != -1) sigPoints.Add(new SignificantPoint(lm.QPeak, EcgPointType.Q_PEAK));
                if (lm.SPeak != -1) sigPoints.Add(new SignificantPoint(lm.SPeak, EcgPointType.S_PEAK));
                if (lm.PPeak != -1) sigPoints.Add(new SignificantPoint(lm.PPeak, EcgPointType.P_PEAK));
                if (lm.TPeak != -1) sigPoints.Add(new SignificantPoint(lm.TPeak, EcgPointType.T_PEAK));

                if (lm.QrsStart != -1) sigPoints.Add(new SignificantPoint(lm.QrsStart, EcgPointType.QRS_START));
                if (lm.QrsEnd != -1) sigPoints.Add(new SignificantPoint(lm.QrsEnd, EcgPointType.QRS_END));
                if (lm.PStart != -1) sigPoints.Add(new SignificantPoint(lm.PStart, EcgPointType.P_START));
                if (lm.PEnd != -1) sigPoints.Add(new SignificantPoint(lm.PEnd, EcgPointType.P_END));
                if (lm.TStart != -1) sigPoints.Add(new SignificantPoint(lm.TStart, EcgPointType.T_START));
                if (lm.TEnd != -1) sigPoints.Add(new SignificantPoint(lm.TEnd, EcgPointType.T_END));
            }

            _editorVm.SetSignificantPoints(sigPoints);
            UpdateCanvasAndPreview();
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title = "Auto-Detect Error",
                Content = $"Failed to detect wave points: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await err.ShowAsync();
        }
    }

    private async void OnImageClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null || _pickOpenImage is null) return;
        var file = await _pickOpenImage();
        if (file is null) return;
        _editorVm.SetReferenceImageUri(file.Path);
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null) return;
        var enBox = new TextBox { PlaceholderText = "Name (English)" };
        var ruBox = new TextBox { PlaceholderText = "Название (Russian)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(enBox);
        panel.Children.Add(ruBox);
        var dialog = new ContentDialog
        {
            Title = "New Pathology",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(enBox.Text))
        {
            var ruName = string.IsNullOrWhiteSpace(ruBox.Text) ? null : ruBox.Text.Trim();
            _editorVm.CreateNewPathology(enBox.Text.Trim(), ruName);
        }
    }

    // ── WFDB / PhysioNet import ───────────────────────────────────────────────

    private async void OnImportWfdbFileClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null || _pickOpenWfdb is null) return;
        var picked = await _pickOpenWfdb();
        if (picked is null) return;

        var headerPath = ResolveHeaderPath(picked.Path);
        if (headerPath is null)
        {
            await ShowError("Import WFDB", "No matching .hea header was found next to the selected file. " +
                "A WFDB record needs its .hea header alongside the signal file.");
            return;
        }

        WfdbRecord record;
        try
        {
            record = await Task.Run(() => WfdbReader.ReadRecord(headerPath));
        }
        catch (Exception ex)
        {
            await ShowError("Import WFDB", $"Could not read the WFDB record:\n{ex.Message}");
            return;
        }

        await ImportRecordAsync(record, Path.GetFileNameWithoutExtension(headerPath));
    }

    private async void OnImportPhysioNetClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null) return;

        var pathBox = new TextBox
        {
            Header = "Project path",
            PlaceholderText = "challenge-2021/1.0.3/training/chapman_shaoxing/g1",
        };
        var recBox = new TextBox { Header = "Record", PlaceholderText = "JS00001" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "Downloads a record straight from physionet.org/files/. The project path is the folder " +
                   "that contains the record (project/version/sub-folders).",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6,
        });
        panel.Children.Add(pathBox);
        panel.Children.Add(recBox);
        panel.Children.Add(progress);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "Download from PhysioNet",
            Content = panel,
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        WfdbRecord? downloaded = null;
        var recordName = "";
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var path = pathBox.Text.Trim();
            var rec = recBox.Text.Trim();
            if (path.Length == 0 || rec.Length == 0)
            {
                args.Cancel = true;
                status.Text = "Enter both a project path and a record name.";
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                d.IsPrimaryButtonEnabled = false;
                progress.IsActive = true;
                status.Text = "Downloading…";
                using var client = new PhysioNetClient();
                downloaded = await client.DownloadRecordAsync(path, rec);
                recordName = rec;
            }
            catch (Exception ex)
            {
                downloaded = null;
                args.Cancel = true;
                status.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                progress.IsActive = false;
                d.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && downloaded is not null)
        {
            await ImportRecordAsync(downloaded, recordName);
        }
    }

    /// <summary>
    /// Confirms the decoded record, lets the author name it, converts it to a pathology, and imports it.
    /// </summary>
    private async Task ImportRecordAsync(WfdbRecord record, string defaultName)
    {
        if (_editorVm is null) return;

        var leadCount = record.Header.Signals.Count(s => Leads.FromToken(s.Description) is not null);
        var nameBox = new TextBox { Header = "Name", Text = DeriveTitle(record, defaultName) };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{record.ChannelCount} signals · {leadCount} recognized ECG leads · " +
                   $"{record.SampleCount} samples @ {record.Header.SamplingFrequency:0.#} Hz",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        panel.Children.Add(nameBox);
        if (leadCount == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No standard 12-lead leads were recognized in this record, so there is nothing to import.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.Red),
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Import ECG record",
            Content = panel,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = leadCount > 0,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
        var file = WfdbConverter.ToPathologyFile(record, defaultName, title);
        var newId = _editorVm.ImportPathology(file);
        if (newId is null)
        {
            await ShowError("Import failed",
                "Could not save the imported pathology. The active data source must be a writable folder.");
        }
    }

    private static string? ResolveHeaderPath(string pickedPath)
    {
        if (pickedPath.EndsWith(".hea", StringComparison.OrdinalIgnoreCase)) return pickedPath;
        var dir = Path.GetDirectoryName(pickedPath);
        if (dir is null) return null;
        var header = Path.Combine(dir, Path.GetFileNameWithoutExtension(pickedPath) + ".hea");
        return File.Exists(header) ? header : null;
    }

    /// <summary>Reads a title from a <c>Title:</c> comment if present, else falls back to the record name.</summary>
    private static string DeriveTitle(WfdbRecord record, string fallback)
    {
        foreach (var comment in record.Header.Comments)
        {
            var sep = comment.IndexOf(':');
            if (sep > 0 && comment[..sep].Trim().Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                var value = comment[(sep + 1)..].Trim();
                if (value.Length > 0) return value;
            }
        }
        return fallback;
    }

    private async Task ShowError(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null) return;
        var file = _editorVm.TargetFile;
        var enBox = new TextBox { Text = file.TitleEn, PlaceholderText = "Name (English)" };
        var ruBox = new TextBox { Text = file.NameRu ?? string.Empty, PlaceholderText = "Название (Russian)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(enBox);
        panel.Children.Add(ruBox);
        var dialog = new ContentDialog
        {
            Title = "Duplicate Pathology",
            Content = panel,
            PrimaryButtonText = "Duplicate",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(enBox.Text))
        {
            var ruName = string.IsNullOrWhiteSpace(ruBox.Text) ? null : ruBox.Text.Trim();
            _editorVm.DuplicateCurrentPathology(enBox.Text.Trim(), ruName);
        }
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

    private async void OnInsertElementClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;

        if (!ConstructorViewModel.IsLeadEditable(_editorVm.FocusedLead))
        {
            var warn = new ContentDialog
            {
                Title = "Insert element",
                Content = "This lead is derived (read-only). Select a primary lead (I, II, V2, V6) first.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await warn.ShowAsync();
            return;
        }

        var items = new (EcgElement Element, string Label)[]
        {
            (EcgElement.PWave, "P wave"),
            (EcgElement.QrsComplex, "QRS complex"),
            (EcgElement.TWave, "T wave"),
            (EcgElement.StSegment, "ST segment"),
            (EcgElement.Baseline, "Baseline (flat)"),
        };

        var combo = new ComboBox
        {
            Header = "Element",
            ItemsSource = items.Select(i => i.Label).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var widthBox = new NumberBox
        {
            Header = "Width (ms)", Minimum = 1, SmallChange = 5, LargeChange = 20,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var heightBox = new NumberBox
        {
            Header = "Height (mV)", SmallChange = 0.05, LargeChange = 0.2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        void ApplyDefaults(int idx)
        {
            var d = EcgElementGenerator.Defaults(items[idx].Element);
            widthBox.Value = d.DurationMs;
            heightBox.Value = d.AmplitudeMv;
        }
        ApplyDefaults(0);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) ApplyDefaults(combo.SelectedIndex); };

        var panel = new StackPanel { Spacing = 8, Width = 280 };
        panel.Children.Add(combo);
        panel.Children.Add(widthBox);
        panel.Children.Add(heightBox);

        var dialog = new ContentDialog
        {
            Title = "Insert element at cursor",
            Content = panel,
            PrimaryButtonText = "Insert",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var sel = combo.SelectedIndex;
        if (sel < 0) return;
        var element = items[sel].Element;
        var defaults = EcgElementGenerator.Defaults(element);
        var width = double.IsNaN(widthBox.Value) ? defaults.DurationMs : widthBox.Value;
        var height = double.IsNaN(heightBox.Value) ? defaults.AmplitudeMv : heightBox.Value;
        _editorVm.InsertElement(element, new EcgElementParams((float)width, (float)height), _monitorVm.MonitorMode.Calibration);
    }

    private async void OnManageElementsClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;
        var lead = _editorVm.FocusedLead;
        var cal = _monitorVm.MonitorMode.Calibration;
        var list = new StackPanel { Spacing = 6, MinWidth = 380 };

        void Apply(int idx, NumberBox w, NumberBox h)
        {
            if (double.IsNaN(w.Value) || double.IsNaN(h.Value)) return;
            _editorVm!.ResizeElement(lead, idx, (float)w.Value, (float)h.Value, cal);
        }

        void Rebuild()
        {
            list.Children.Clear();
            var elements = _editorVm!.ElementsFor(lead);
            if (elements.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"No elements placed on lead {lead}. Use “Insert element” to add one.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                });
                return;
            }
            for (var i = 0; i < elements.Count; i++)
            {
                var idx = i;
                var el = elements[i];
                var widthMs = el.Length / cal.SampleRateHz * 1000f;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = ElementLabel(el.Type), Width = 90, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 6),
                });

                var widthBox = new NumberBox
                {
                    Header = "Width (ms)", Value = Math.Round(widthMs), Minimum = 1,
                    SmallChange = 5, LargeChange = 20, Width = 120,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                };
                var heightBox = new NumberBox
                {
                    Header = "Height (mV)", Value = el.AmplitudeMv,
                    SmallChange = 0.05, LargeChange = 0.2, Width = 120,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                };
                widthBox.ValueChanged += (_, _) => Apply(idx, widthBox, heightBox);
                heightBox.ValueChanged += (_, _) => Apply(idx, widthBox, heightBox);
                row.Children.Add(widthBox);
                row.Children.Add(heightBox);

                var del = new Button
                {
                    Content = new SymbolIcon(Symbol.Delete),
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                del.Click += (_, _) => { _editorVm!.RemoveElement(lead, idx); Rebuild(); };
                row.Children.Add(del);

                list.Children.Add(row);
            }
        }

        Rebuild();
        var dialog = new ContentDialog
        {
            Title = $"Elements — lead {lead}",
            Content = new ScrollViewer
            {
                Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ── Element palette ─────────────────────────────────────────────────────

    private void BuildPalette()
    {
        _palette.Children.Add(new TextBlock
        {
            Text = "Insert:", VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7, Margin = new Thickness(0, 0, 4, 0),
        });

        var items = new (EcgElement Element, string Label)[]
        {
            (EcgElement.PWave, "P"),
            (EcgElement.QrsComplex, "QRS"),
            (EcgElement.TWave, "T"),
            (EcgElement.StSegment, "ST"),
            (EcgElement.Baseline, "Base"),
        };
        foreach (var (element, label) in items)
        {
            var captured = element;
            var button = new Button { Content = label, MinWidth = 44 };
            ToolTipService.SetToolTip(button, $"Insert {ElementLabel(element)} at the cursor (default size)");
            button.Click += (_, _) => InsertElementFromPalette(captured);
            _paletteButtons.Add(button);
            _palette.Children.Add(button);
        }
    }

    private void InsertElementFromPalette(EcgElement element)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;
        if (!ConstructorViewModel.IsLeadEditable(_editorVm.FocusedLead)) return;
        _editorVm.InsertElement(element, EcgElementGenerator.Defaults(element), _monitorVm.MonitorMode.Calibration);
    }

    /// <summary>Enables the palette only when a primary (editable) lead of a loaded pathology is focused.</summary>
    private void RefreshPalette()
    {
        var enabled = _editorVm?.TargetFile is not null
            && ConstructorViewModel.IsLeadEditable(_editorVm.FocusedLead);
        foreach (var button in _paletteButtons) button.IsEnabled = enabled;
    }

    private static string ElementLabel(EcgElement type) => type switch
    {
        EcgElement.PWave => "P wave",
        EcgElement.QrsComplex => "QRS",
        EcgElement.TWave => "T wave",
        EcgElement.StSegment => "ST segment",
        EcgElement.Baseline => "Baseline",
        _ => type.ToString(),
    };

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

    private async void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null) return;

        // Index 0 = "no group"; the rest mirror PathologyGroups.OrderedKeys.
        var keys = new List<string?> { null };
        keys.AddRange(PathologyGroups.OrderedKeys);
        var labels = new List<string> { AppStrings.GroupNone };
        labels.AddRange(PathologyGroups.OrderedKeys.Select(PathologyGroups.DisplayName));

        var current = keys.IndexOf(_editorVm.CurrentGroup);
        var combo = new ComboBox
        {
            Header = AppStrings.GroupEditTitle,
            ItemsSource = labels,
            SelectedIndex = current < 0 ? 0 : current,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // Create-a-new-group field: if filled in, it takes precedence over the dropdown.
        var newGroupBox = new TextBox
        {
            Header = AppStrings.GroupCreateNew,
            PlaceholderText = AppStrings.GroupEditTitle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Width = 320, Spacing = 12 };
        panel.Children.Add(combo);
        panel.Children.Add(newGroupBox);

        var dialog = new ContentDialog
        {
            Title = AppStrings.GroupEditTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = newGroupBox.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            var newKey = PathologyGroups.CreateGroup(newName);
            if (newKey is not null) _editorVm.SetGroup(newKey);
            return;
        }

        var idx = combo.SelectedIndex;
        if (idx >= 0 && idx < keys.Count) _editorVm.SetGroup(keys[idx]);
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
