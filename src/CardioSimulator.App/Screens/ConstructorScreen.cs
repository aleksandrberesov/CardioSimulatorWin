using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.Theming;
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
    private readonly Button _renameButton = new() { Content = new SymbolIcon(Symbol.Edit), Visibility = Visibility.Collapsed };
    private readonly Button _groupButton = new() { Content = new SymbolIcon(Symbol.Tag), Visibility = Visibility.Collapsed };
    // Contact/patient glyph (Segoe MDL2 "Contact") for the clinical-case editor - a person reads as
    // "patient case". The old U+ECAD glyph is a flame, unrelated to clinical cases and reported as unclear.
    private readonly Button _clinicalCaseButton = new() { Content = new FontIcon { Glyph = "\uE77B", FontSize = 16 }, Visibility = Visibility.Collapsed };
    private readonly Button _descriptionButton = new() { Content = new FontIcon { Glyph = "\uE946", FontSize = 16 }, Visibility = Visibility.Collapsed };
    // C2 (customer 28-08): doctor-verification status of the current rhythm, in the top panel.
    private readonly ComboBox _verificationCombo = new() { Visibility = Visibility.Collapsed, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private bool _suppressVerificationEvent;
    private readonly Button _duplicateButton = new() { Content = new SymbolIcon(Symbol.Copy), Visibility = Visibility.Collapsed };
    private readonly Button _deleteButton = new() { Content = new SymbolIcon(Symbol.Delete), Visibility = Visibility.Collapsed };
    private readonly Button _calcDerivedButton = new() { Content = new SymbolIcon(Symbol.Calculator), Visibility = Visibility.Collapsed };
    private readonly Button _viewAllButton = new() { Content = new FontIcon { Glyph = "\uE8A9", FontSize = 16 }, Visibility = Visibility.Collapsed };
    private readonly Button _insertElementButton = new() { Content = new SymbolIcon(Symbol.AllApps), Visibility = Visibility.Collapsed };
    private readonly Button _manageElementsButton = new() { Content = new SymbolIcon(Symbol.List), Visibility = Visibility.Collapsed };
    private readonly Button _undoButton = new() { Content = new SymbolIcon(Symbol.Undo), Visibility = Visibility.Collapsed };
    private readonly Button _redoButton = new() { Content = new SymbolIcon(Symbol.Redo), Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = new SymbolIcon(Symbol.Save), Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = AppStrings.CtorRevertLead, Visibility = Visibility.Collapsed };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 4, 8, 4) };
    private readonly StackPanel _palette = new() { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(16, 2, 16, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Button> _paletteButtons = new();
    private readonly Grid _root = new();
    private Grid _contentRoot = null!;

    // ── Read-only "all 12 leads" preview overlay ───────────────────────────
    // A full-surface, static (non-scrolling) 12-lead monitor render of the pathology being edited.
    // It has no pointer/edit wiring, so it is purely a look-don't-touch overview of every lead.
    private readonly EcgMonitorControl _allLeadsMonitor = new();
    private readonly TextBlock _allLeadsTitle = new()
    {
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private Grid _allLeadsOverlay = null!;
    private Grid _allLeadsTopBar = null!;

    // ── ToolModePanel sidebar (rightmost column, 56 px) ────────────────────
    private readonly ToolModePanelControl _toolModePanel = new();

    // ── Mode-specific panel host (swapped on ToolMode change) ─────────────
    private readonly Border _modePanelHost = new() { Width = 240, VerticalAlignment = VerticalAlignment.Stretch };

    // Draw (Trace) mode panel controls
    private readonly Button _drawAutoDetectBtn = new() { Content = AppStrings.CtorAutoDetect, Visibility = Visibility.Collapsed };
    private readonly Button _drawUndoBtn = new() { Content = new SymbolIcon(Symbol.Undo) };
    private readonly Border _ghostAcceptArea = new() { Visibility = Visibility.Collapsed };
    private readonly Button _applyGhostBtn = new() { Content = AppStrings.CommonApply };
    private readonly Button _cancelGhostBtn = new() { Content = AppStrings.CommonCancel };

    // Photo mode panel controls
    private readonly Button _photoLoadBtn = new() { Content = new SymbolIcon(Symbol.OpenFile) };
    private readonly CheckBox _photoVisibleCheck = new() { Content = AppStrings.CtorPhotoVisible };
    private readonly CheckBox _photoLockCheck = new() { Content = AppStrings.CtorPhotoLock };
    private readonly Button _photoResetBtn = new() { Content = AppStrings.CtorReset };
    private readonly Button _photoDeleteBtn = new() { Content = new SymbolIcon(Symbol.Delete) };
    private readonly Slider _alphaSlider = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Width = 200 };
    private readonly Slider _scaleSlider = new() { Minimum = 0.2, Maximum = 5.0, StepFrequency = 0.05, Width = 200 };
    private readonly Slider _rotationSlider = new() { Minimum = -180, Maximum = 180, StepFrequency = 1, Width = 200 };
    private readonly StackPanel _photoSlidersArea = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly TextBlock _photoNoImageLabel = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) };

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
    private string? _lastTargetClinicalCase;

    public ConstructorScreen()
    {
        BuildLayout();
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => AppTheme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        RefreshTabs();
        if (_allLeadsOverlay is not null)
        {
            _allLeadsOverlay.Background = AppTheme.AppPageBackground;
            if (_allLeadsTopBar is not null) _allLeadsTopBar.Background = AppTheme.PanelBackground;
            _allLeadsTitle.Foreground = AppTheme.TextPrimary;
        }
    }

    private void BuildLayout()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // toolbar
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // lead tabs
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // element palette
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // canvas

        // ── Toolbar ─────────────────────────────────────────────────────────
        // Two rows: the pathology title on its own line, and the action buttons
        // ("settings panel") on the line below. Keeping the title out of the button
        // row means a long pathology name can no longer push the buttons off-screen.
        var toolbarColumn = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Thickness(16, 8, 16, 8),
        };
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        toolbarColumn.Children.Add(_title);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        _newButton.Click += OnNewClick;
        toolbar.Children.Add(_newButton);

        _renameButton.Click += OnRenameClick;
        toolbar.Children.Add(_renameButton);
        _groupButton.Click += OnGroupClick;
        ToolTipService.SetToolTip(_groupButton, AppStrings.GroupEditTitle);
        toolbar.Children.Add(_groupButton);

        _clinicalCaseButton.Click += OnClinicalCaseClick;
        ToolTipService.SetToolTip(_clinicalCaseButton, AppStrings.ClinicalEditTooltip);
        toolbar.Children.Add(_clinicalCaseButton);

        _descriptionButton.Click += OnDescriptionClick;
        ToolTipService.SetToolTip(_descriptionButton, AppStrings.DescriptionEditTooltip);
        toolbar.Children.Add(_descriptionButton);

        _verificationCombo.Items.Add(new ComboBoxItem { Content = AppStrings.VerifyStatusVerified, Tag = VerificationStatus.Verified });
        _verificationCombo.Items.Add(new ComboBoxItem { Content = AppStrings.VerifyStatusReview, Tag = VerificationStatus.InReview });
        _verificationCombo.Items.Add(new ComboBoxItem { Content = AppStrings.VerifyStatusUnchecked, Tag = VerificationStatus.Unchecked });
        ToolTipService.SetToolTip(_verificationCombo, AppStrings.VerifyStatusTooltip);
        _verificationCombo.SelectionChanged += OnVerificationChanged;
        toolbar.Children.Add(_verificationCombo);

        _duplicateButton.Click += OnDuplicateClick;
        toolbar.Children.Add(_duplicateButton);
        _deleteButton.Click += OnDeleteClick;
        toolbar.Children.Add(_deleteButton);
        ToolTipService.SetToolTip(_calcDerivedButton, AppStrings.CalcDerivedLeads);
        _calcDerivedButton.Click += OnCalcDerivedClick;
        toolbar.Children.Add(_calcDerivedButton);

        // "All leads" lives in the lead-button row (see RefreshTabs), not the toolbar.
        ToolTipService.SetToolTip(_viewAllButton, AppStrings.ConstructorViewAllLeads);
        _viewAllButton.Click += OnViewAllClick;
        _viewAllButton.Margin = new Thickness(8, 0, 0, 0);
        // Added once here and NEVER removed/re-added: re-parenting a persistent field
        // UIElement into a rebuilt panel crashes WinUI (0xc000027b). RefreshTabs only
        // swaps the per-lead buttons and leaves this button in place as the last child.
        _tabs.Children.Add(_viewAllButton);

        ToolTipService.SetToolTip(_insertElementButton, AppStrings.CtorInsertElement);
        _insertElementButton.Click += OnInsertElementClick;
        toolbar.Children.Add(_insertElementButton);

        ToolTipService.SetToolTip(_manageElementsButton, AppStrings.CtorManageElements);
        _manageElementsButton.Click += OnManageElementsClick;
        toolbar.Children.Add(_manageElementsButton);

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

        ToolTipService.SetToolTip(_saveButton, AppStrings.CommonSave);
        _saveButton.Click += async (_, _) => { if (_editorVm is not null) await _editorVm.SaveAsync(); };
        _revertButton.Click += (_, _) => _editorVm?.RevertLead(_editorVm.FocusedLead);
        toolbar.Children.Add(_saveButton);
        toolbar.Children.Add(_revertButton);

        // The button row scrolls horizontally as a last resort (very narrow window /
        // many visible buttons) so the "settings panel" is always reachable.
        var toolbarScroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = toolbar,
        };
        toolbarColumn.Children.Add(toolbarScroll);
        Grid.SetRow(toolbarColumn, 0);
        content.Children.Add(toolbarColumn);

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
        // The mode-specific panel (which hosts the significant-points editor while in Points mode)
        // and the vertical tool-mode icon strip sit on the RIGHT of the canvas, beside each other.
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

        // Build the read-only all-leads overlay and drop it into the canvas cell (content row 3),
        // on top of the editable canvas but below the toolbar/tabs/palette and the rhythm drawer.
        BuildAllLeadsOverlay();
        content.Children.Add(_allLeadsOverlay);

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
        // Clicking a row in the panel's marked-points list jumps to that sample (the list replaces
        // the old floating SignificantPointsDrawer).
        _pointPanel.PointSelected += index => _editorVm?.SelectIndex(index);
        // Changing the detect/ruler window redraws the editable lead's time ruler.
        _pointPanel.DetectWindowChanged += UpdateCanvasAndPreview;
        _drawer.RhythmSelected += async (_, entry) => await OnRhythmChosen(entry);
        _toolModePanel.ModeChanged += mode => { if (_editorVm is not null) _editorVm.ToolMode = mode; };
        // Tips: switches into the inline tips-authoring panel + canvas placement mode (arrow,
        // lead/graph/segment highlight, guide lines, label, freeform area, points).
        _toolModePanel.TipsClick += () => { if (_editorVm is not null) _editorVm.ToolMode = ToolMode.Tips; };
        _editable.TipPlaced += OnTipPlaced;

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
        ghostInner.Children.Add(new TextBlock { Text = AppStrings.CtorApplyGhost, TextWrapping = TextWrapping.Wrap });
        var ghostBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        ghostBtns.Children.Add(_applyGhostBtn);
        ghostBtns.Children.Add(_cancelGhostBtn);
        ghostInner.Children.Add(ghostBtns);
        _ghostAcceptArea.CornerRadius = new CornerRadius(6);
        _ghostAcceptArea.Background = new SolidColorBrush(new Color { A = 0xFF, R = 0xCB, G = 0xE5, B = 0xCC });
        _ghostAcceptArea.Child = ghostInner;

        // Wire photo sliders area.
        _photoSlidersArea.Children.Add(LabeledSlider(AppStrings.CtorPhotoOpacity, _alphaSlider));
        _photoSlidersArea.Children.Add(LabeledSlider(AppStrings.CtorPhotoScale, _scaleSlider));
        _photoSlidersArea.Children.Add(LabeledSlider(AppStrings.CtorPhotoRotation, _rotationSlider));
        _photoNoImageLabel.Text = AppStrings.CtorPhotoNoImage;
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
        col.Children.Add(new TextBlock { Text = AppStrings.CtorToolSelect, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        return MakePanelBorder(col);
    }

    private UIElement BuildPositionPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = AppStrings.CtorToolPosition, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        col.Children.Add(new TextBlock { Text = AppStrings.CtorPositionHelp, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        return MakePanelBorder(col);
    }

    private UIElement BuildDrawPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = AppStrings.CtorToolTrace, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });

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
        col.Children.Add(new TextBlock { Text = AppStrings.SegToolPan, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());
        col.Children.Add(new TextBlock
        {
            Text = AppStrings.CtorPanHelp,
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0),
        });
        var resetBtn = new Button { Content = AppStrings.CtorResetView, Margin = new Thickness(0, 8, 0, 0) };
        resetBtn.Click += (_, _) => _editable.ResetView();
        col.Children.Add(resetBtn);
        return MakePanelBorder(col);
    }

    private UIElement BuildPhotoPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = AppStrings.CtorToolImage, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        ToolTipService.SetToolTip(_photoLoadBtn, AppStrings.CtorPhotoLoadTip);
        ToolTipService.SetToolTip(_photoDeleteBtn, AppStrings.CtorPhotoRemoveTip);
        ToolTipService.SetToolTip(_photoResetBtn, AppStrings.CtorPhotoResetTip);
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

    // ── Tips authoring panel (inline, in the mode-panel host) ────────────────

    private static readonly (TipOverlayKind Kind, Func<string> Label)[] TipKinds =
    [
        (TipOverlayKind.Arrow,           () => AppStrings.MonitorTipsTypeArrow),
        (TipOverlayKind.LeadArea,        () => AppStrings.MonitorTipsTypeLeadArea),
        (TipOverlayKind.GraphArea,       () => AppStrings.MonitorTipsTypeGraphAreaRect),
        (TipOverlayKind.VerticalLines,   () => AppStrings.MonitorTipsTypeVerticalLines),
        (TipOverlayKind.HorizontalLines, () => AppStrings.MonitorTipsTypeHorizontalLines),
        (TipOverlayKind.Label,           () => AppStrings.MonitorTipsTypeLabel),
        (TipOverlayKind.FreeformArea,    () => AppStrings.MonitorTipsTypeFreeformArea),
        (TipOverlayKind.EcgPart,         () => AppStrings.MonitorTipsTypeEcgPart),
        (TipOverlayKind.Points,          () => AppStrings.MonitorTipsTypePoints),
    ];

    /// <summary>
    /// The inline tips-authoring panel shown in the mode-panel host while <see cref="ToolMode.Tips"/>
    /// is active: pick an element kind (+ a lead for the whole-lead highlight, + an end-cap for the
    /// guide lines), then draw it on the trace. Replaces the old floating popup — it reuses the same
    /// right-hand side-panel space as the other tool modes.
    /// </summary>
    private UIElement BuildTipsPanel()
    {
        var col = new StackPanel { Padding = new Thickness(8), Spacing = 4 };
        col.Children.Add(new TextBlock { Text = AppStrings.ConstructorTipsTitle, FontWeight = FontWeights.SemiBold, Opacity = 0.7 });
        col.Children.Add(Divider());

        // Lead picker (for the whole-lead highlight) and end-cap selector (for the guide lines) are
        // built first so the kind radios can toggle their visibility as the selection changes.
        var leadHost = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        leadHost.Children.Add(new TextBlock { Text = AppStrings.MonitorTipsLeadPickHeader, FontSize = 12, Opacity = 0.7 });
        var leadCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var lead in Leads.All) leadCombo.Items.Add(lead);
        leadCombo.SelectedItem = _editorVm?.SelectedTipLead ?? Lead.aVL;
        leadCombo.SelectionChanged += (_, _) => { if (_editorVm is not null && leadCombo.SelectedItem is Lead l) { _editorVm.SelectedTipLead = l; UpdateCanvasAndPreview(); } };
        leadHost.Children.Add(leadCombo);

        var capHost = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        capHost.Children.Add(new TextBlock { Text = AppStrings.MonitorTipsLineCapHeader, FontSize = 12, Opacity = 0.7 });
        var capCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        capCombo.Items.Add(AppStrings.MonitorTipsLineCapPlain);
        capCombo.Items.Add(AppStrings.MonitorTipsLineCapDots);
        capCombo.Items.Add(AppStrings.MonitorTipsLineCapArrows);
        capCombo.SelectedIndex = (int)(_editorVm?.SelectedTipEndCap ?? TipLineEndCap.Plain);
        capCombo.SelectionChanged += (_, _) => { if (_editorVm is not null && capCombo.SelectedIndex >= 0) { _editorVm.SelectedTipEndCap = (TipLineEndCap)capCombo.SelectedIndex; UpdateCanvasAndPreview(); } };
        capHost.Children.Add(capCombo);

        void SyncExtras(TipOverlayKind kind)
        {
            leadHost.Visibility = kind == TipOverlayKind.LeadArea ? Visibility.Visible : Visibility.Collapsed;
            capHost.Visibility = kind is TipOverlayKind.VerticalLines or TipOverlayKind.HorizontalLines
                ? Visibility.Visible : Visibility.Collapsed;
        }

        var current = _editorVm?.SelectedTipKind ?? TipOverlayKind.Arrow;
        foreach (var (kind, label) in TipKinds)
        {
            var rb = new RadioButton { Content = label(), GroupName = "tipkind", Tag = kind, IsChecked = kind == current, Padding = new Thickness(4, 2, 4, 2), MinHeight = 0 };
            rb.Checked += (_, _) =>
            {
                if (_editorVm is null) return;
                _editorVm.SelectedTipKind = kind;
                SyncExtras(kind);
                UpdateCanvasAndPreview();
            };
            col.Children.Add(rb);
        }

        col.Children.Add(leadHost);
        col.Children.Add(capHost);
        SyncExtras(current);

        col.Children.Add(Divider());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var undoBtn = new Button { Content = AppStrings.ConstructorTipsUndo };
        undoBtn.Click += (_, _) => { _editorVm?.RemoveLastTip(); UpdateCanvasAndPreview(); };
        var clearBtn = new Button { Content = AppStrings.ConstructorTipsClear };
        clearBtn.Click += (_, _) => { _editorVm?.ClearTips(); UpdateCanvasAndPreview(); };
        actions.Children.Add(undoBtn);
        actions.Children.Add(clearBtn);
        col.Children.Add(actions);

        // Comments / explanations window (the "Видим:" text list shown on the monitor).
        var commentsBtn = new Button
        {
            Content = AppStrings.ConstructorTipsComments,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        commentsBtn.Click += async (_, _) => await ShowTipCommentsDialog();
        col.Children.Add(commentsBtn);

        col.Children.Add(new TextBlock { Text = AppStrings.ConstructorTipsNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) });

        var scroll = new ScrollViewer
        {
            Content = col,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return MakePanelBorder(scroll);
    }

    /// <summary>Commits a tip placed on the canvas. Arrow/label kinds prompt for a caption first.</summary>
    private async void OnTipPlaced(TipOverlay overlay)
    {
        if (_editorVm is null) return;
        if (overlay.Kind is TipOverlayKind.Label or TipOverlayKind.Arrow)
        {
            var text = await PromptTipText();
            if (text is null) return; // cancelled → discard the placement
            var trimmed = text.Trim();
            overlay = overlay with { Text = trimmed.Length == 0 ? null : trimmed };
        }
        // Anchor the overlay to the lead it was drawn on so it renders in that cell on the monitor
        // grid (LeadArea keeps its separately-chosen highlight lead).
        if (overlay.Kind != TipOverlayKind.LeadArea)
            overlay = overlay with { Lead = _editorVm.FocusedLead };
        _editorVm.AddTip(overlay);
        UpdateCanvasAndPreview();
    }

    /// <summary>Opens the comments/explanations window ("Видим:" list). One explanation per line; the
    /// text is saved with the pathology and rendered as a card on the monitor.</summary>
    private async Task ShowTipCommentsDialog()
    {
        if (_editorVm is null) return;
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            Width = 380,
            Text = string.Join("\n", _editorVm.TipComments),
            PlaceholderText = "1. …\n2. …",
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = AppStrings.ConstructorTipsCommentsHelp, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.ConstructorTipsComments,
            Content = panel,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var lines = box.Text.Replace("\r\n", "\n").Split('\n');
        _editorVm.SetTipComments(lines);
    }

    private async Task<string?> PromptTipText()
    {
        var box = new TextBox { Header = AppStrings.ConstructorTipsTextPrompt, AcceptsReturn = false };
        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.ConstructorTipsTextPrompt,
            Content = box,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
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
            ToolMode.Tips     => BuildTipsPanel(),
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

        // The Constructor drives the drawer selection one-way from the editor's TargetFile and must
        // never let list filtering silently switch the pathology being edited (that would discard
        // unsaved edits). Explicit taps are still handled — and guarded — via RhythmSelected.
        _drawer.AutoSelectOnFilter = false;
        _drawer.DisplayLanguage = appVm.SelectedLanguage;
        _drawer.SetRhythms(rhythmVm.Rhythms);
        _drawer.SelectedId = editorVm.TargetFile?.Id;
        // Show the drawer in the list the edited pathology actually belongs to.
        if (editorVm.TargetFile is { } initial)
            _drawer.ClinicalMode = !string.IsNullOrWhiteSpace(initial.ClinicalCase);
        _lastTargetId = editorVm.TargetFile?.Id;
        _lastTargetTitleEn = editorVm.TargetFile?.TitleEn;
        _lastTargetNameRu = editorVm.TargetFile?.NameRu;
        _lastTargetGroup = editorVm.TargetFile?.Group;
        _lastTargetClinicalCase = editorVm.TargetFile?.ClinicalCase;

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

        // Prompt to save when leaving the Constructor (mode switch) with unsaved edits.
        appVm.LeaveGuardAsync = ConfirmLeaveAsync;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appVm is not null && _appVm.LeaveGuardAsync == ConfirmLeaveAsync) _appVm.LeaveGuardAsync = null;
    }

    /// <summary>The leave-guard body: prompt to save/discard when there are unsaved edits.</summary>
    private Task<bool> ConfirmLeaveAsync() =>
        _editorVm is null ? Task.FromResult(true) : UnsavedChangesDialog.ConfirmAsync(XamlRoot, _editorVm);

    /// <summary>
    /// Handles a rhythm tapped in the drawer. Switching to a <em>different</em> pathology while the
    /// current one has unsaved edits prompts Save / Don't save / Cancel first; Cancel keeps the editor
    /// on the current pathology and restores the drawer highlight to it.
    /// </summary>
    private async Task OnRhythmChosen(PathologyEntry entry)
    {
        if (_editorVm is null) return;
        var currentId = _editorVm.TargetFile?.Id;
        if (entry.Id == currentId) return; // re-selecting the same pathology — nothing to guard

        if (_editorVm.HasUnsavedChanges)
        {
            if (!await UnsavedChangesDialog.ConfirmAsync(XamlRoot, _editorVm))
            {
                // Cancelled — stay on the current pathology and undo the drawer's selection move.
                _drawer.SelectedId = currentId;
                return;
            }
        }

        _editorVm.SelectPathology(entry.Id);
    }

    // ── Property change handlers ────────────────────────────────────────────

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.SelectedLanguage) && _appVm is not null)
        {
            _drawer.DisplayLanguage = _appVm.SelectedLanguage;
            if (_rhythmVm is not null)
            {
                _drawer.SetRhythms(_rhythmVm.Rhythms);
                _drawerHasLivePatch = false; // list now matches the saved dataset
            }
            UpdateCanvasAndPreview();
            if (IsAllLeadsOverlayOpen) RefreshAllLeadsOverlay();
        }
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorViewModel.MonitorMode))
        {
            UpdateCanvasAndPreview();
            // A filter/speed change while the read-only overview is up refreshes it live too.
            if (IsAllLeadsOverlayOpen) RefreshAllLeadsOverlay();
        }
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rhythmVm is null) return;
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms))
        {
            _drawer.SetRhythms(_rhythmVm.Rhythms);
            _drawerHasLivePatch = false; // clean dataset just loaded; re-apply a patch only if still edited
            RefreshRhythmListNames();
        }
    }

    /// <summary>True when the drawer list currently shows an in-memory (unsaved) label/group patch for the
    /// edited pathology that differs from the saved dataset — so a later switch knows it must rebuild once
    /// to drop the patch, and an unedited switch knows it can skip the rebuild entirely.</summary>
    private bool _drawerHasLivePatch;

    /// <summary>
    /// Patches the drawer's rhythm list so the in-memory (unsaved) name and group of the currently
    /// edited pathology are reflected immediately — a rename re-labels the row, and a group change
    /// moves it to its new section — before the file is saved.
    /// </summary>
    private void RefreshRhythmListNames()
    {
        var file = _editorVm?.TargetFile;
        if (file is null || _rhythmVm is null) return;

        var stored = _rhythmVm.Rhythms.FirstOrDefault(e => e.Id == file.Id);
        var differs = stored is not null
            && (stored.TitleEn != file.TitleEn || stored.NameRu != file.NameRu
                || stored.Group != file.Group || stored.ClinicalCase != file.ClinicalCase);

        // The edited pathology's in-memory metadata already matches the saved list and the drawer isn't
        // showing a stale patch to undo → the rebuilt list would be identical, so leave it (and its scroll)
        // untouched. Rebuilding here on every plain pathology switch is what reset the scroll and made the
        // list visibly jump; the highlight move is handled in place by the panel's item-click.
        if (!differs && !_drawerHasLivePatch) return;

        var patched = _rhythmVm.Rhythms
            .Select(e => e.Id == file.Id
                ? e with { TitleEn = file.TitleEn, NameRu = file.NameRu, Group = file.Group, ClinicalCase = file.ClinicalCase }
                : e)
            .ToList();
        // Preserve scroll: the edited/selected row is already visible, so a live rename or a switch that
        // only drops a stale patch must not snap the list to the top.
        _drawer.SetRhythms(patched, preserveScroll: true);
        _drawerHasLivePatch = differs;
    }

    private async void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConstructorViewModel.TargetFile):
                var tf = _editorVm?.TargetFile;
                _drawer.SelectedId = tf?.Id;
                if (tf?.Id != _lastTargetId || tf?.TitleEn != _lastTargetTitleEn
                    || tf?.NameRu != _lastTargetNameRu || tf?.Group != _lastTargetGroup
                    || tf?.ClinicalCase != _lastTargetClinicalCase)
                {
                    _lastTargetId = tf?.Id;
                    _lastTargetTitleEn = tf?.TitleEn;
                    _lastTargetNameRu = tf?.NameRu;
                    _lastTargetGroup = tf?.Group;
                    _lastTargetClinicalCase = tf?.ClinicalCase;
                    RefreshRhythmListNames();
                    // Keep the drawer's clinical/rhythm filter following the edited pathology so it
                    // always appears in its correct list — e.g. giving it a clinical case moves it out
                    // of the plain-rhythm list into the clinical-cases list (still selected + visible).
                    if (tf is not null) _drawer.ClinicalMode = !string.IsNullOrWhiteSpace(tf.ClinicalCase);
                }
                UpdateCanvasAndPreview();
                UpdateToolbar();
                // Selecting a different rhythm in the still-visible list refreshes the open preview.
                if (IsAllLeadsOverlayOpen) RefreshAllLeadsOverlay();
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
                // Edits reachable while the preview is up (e.g. Calc Derived Leads) update it too.
                if (IsAllLeadsOverlayOpen) RefreshAllLeadsOverlay();
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
            case nameof(ConstructorViewModel.SelectedTipKind):
            case nameof(ConstructorViewModel.SelectedTipEndCap):
            case nameof(ConstructorViewModel.SelectedTipLead):
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

        var title = file is null
            ? AppStrings.CtorNoPathology
            : _appVm.SelectedLanguage == DomainLanguage.RU ? file.ResolvedNameRu ?? file.TitleEn : file.TitleEn;
        _title.Text = file?.Number is { } n ? $"{n} {title}" : title;
        // Full title on hover, in case a very long name is ellipsized on its row.
        ToolTipService.SetToolTip(_title, _title.Text);

        LeadStream? stream = null;
        if (file is not null && file.Leads.TryGetValue(_editorVm.FocusedLead, out var s)) stream = s;

        var points = file?.SignificantPoints ?? Array.Empty<SignificantPoint>();
        var sampleRate = _monitorVm.MonitorMode.Calibration.SampleRateHz;

        // Panel first: it may clamp the window to the lead length, and the editor's ruler reads it back.
        _pointPanel.SetData(points, stream is null ? null : _editorVm.SelectedIndex, sampleRate,
            stream?.Samples.Length ?? 0);
        var window = _pointPanel.DetectWindowSeconds;

        // Clip the drawn overlay to the chosen window (applies to auto-detected AND manually marked
        // points; the stored file keeps them all). Full (null) shows everything.
        var overlayPoints = points;
        if (window is { } ws && sampleRate > 0)
        {
            var limit = (int)Math.Round(ws * sampleRate);
            overlayPoints = points.Where(p => p.Index < limit).ToList();
        }
        _editable.SetData(stream, _baseline, _monitorVm.MonitorMode, overlayPoints, _editorVm.SelectedIndex,
            _editorVm.ImageTransform, _editorVm.ToolMode, _editorVm.GhostTrace, (float?)window,
            _editorVm.Tips, _editorVm.SelectedTipKind, _editorVm.SelectedTipEndCap, _editorVm.SelectedTipLead);

        IReadOnlyList<float> previewValues = stream is null
            ? Array.Empty<float>()
            : stream.Samples.Select(v => (float)(v - _baseline)).ToArray();
        // Show the same display filter the bottom panel selects (None passes the trace through unchanged).
        previewValues = EcgDisplayFilter.Filter(previewValues, _monitorVm.MonitorMode);
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
        _clinicalCaseButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _descriptionButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _verificationCombo.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        if (hasTarget) SyncVerificationCombo();
        _duplicateButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _deleteButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _calcDerivedButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _viewAllButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _insertElementButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        _manageElementsButton.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;

        _undoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _redoButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        _undoButton.IsEnabled = _editorVm.CanUndo(_editorVm.FocusedLead);
        _redoButton.IsEnabled = _editorVm.CanRedo(_editorVm.FocusedLead);

        RefreshPalette();
    }

    /// <summary>Reflects the current rhythm's verification status into the top-panel dropdown without
    /// re-triggering a write (academic rhythms show as Verified by default).</summary>
    private void SyncVerificationCombo()
    {
        if (_editorVm is null) return;
        _suppressVerificationEvent = true;
        var status = _editorVm.CurrentVerification;
        _verificationCombo.SelectedItem = _verificationCombo.Items
            .Cast<ComboBoxItem>().FirstOrDefault(i => (VerificationStatus?)i.Tag == status);
        _suppressVerificationEvent = false;
    }

    private void OnVerificationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVerificationEvent || _editorVm is null) return;
        if ((_verificationCombo.SelectedItem as ComboBoxItem)?.Tag is VerificationStatus status)
        {
            _editorVm.SetVerification(status);
            UpdateToolbar();  // reflect the metadata-dirty save button
        }
    }

    // ── Read-only all-leads preview ───────────────────────────────────────────

    /// <summary>
    /// Builds the full-surface overlay that hosts the static 12-lead monitor render plus a top bar
    /// (pathology title + close). Constructed once; shown/hidden by <see cref="OnViewAllClick"/>.
    /// </summary>
    private void BuildAllLeadsOverlay()
    {
        var root = new Grid
        {
            Background = AppTheme.AppPageBackground,
            Visibility = Visibility.Collapsed,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = new Grid
        {
            Height = 56,
            Padding = new Thickness(16, 0, 8, 0),
            Background = AppTheme.PanelBackground,
        };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _allLeadsTitle.Foreground = AppTheme.TextPrimary;
        Grid.SetColumn(_allLeadsTitle, 0);
        topBar.Children.Add(_allLeadsTitle);
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(close, AppStrings.CommonClose);
        close.Click += (_, _) => CloseAllLeadsOverlay();
        Grid.SetColumn(close, 1);
        topBar.Children.Add(close);
        Grid.SetRow(topBar, 0);
        root.Children.Add(topBar);

        var surface = new Border { Margin = new Thickness(16), Child = _allLeadsMonitor };
        Grid.SetRow(surface, 1);
        root.Children.Add(surface);

        // The overlay covers only the canvas working area (content row 3), so the toolbar, lead
        // tabs, element palette, and the left rhythm drawer all stay visible around it.
        Grid.SetRow(root, 3);
        _allLeadsTopBar = topBar;
        _allLeadsOverlay = root;
    }

    /// <summary>
    /// Opens the read-only all-leads preview: a static (non-scrolling) 12-lead grid render of the
    /// pathology currently being edited, reflecting any unsaved edits. There is no pointer or edit
    /// wiring on this monitor, so it can only be viewed — the edit tools stay on the main canvas.
    /// </summary>
    private void OnViewAllClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null || _appVm is null) return;

        RefreshAllLeadsOverlay();

        // The rhythm drawer stays put (it lives outside the canvas cell the overlay covers).
        _allLeadsOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// (Re)populates the preview title, mode, and waveforms from the pathology currently being
    /// edited. Called on open and again whenever the target/edits change while the overlay is up, so
    /// selecting a different rhythm in the still-visible list refreshes the preview live.
    /// </summary>
    private void RefreshAllLeadsOverlay()
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null || _appVm is null) return;

        var file = _editorVm.TargetFile;
        var title = _appVm.SelectedLanguage == DomainLanguage.RU
            ? file.ResolvedNameRu ?? file.TitleEn
            : file.TitleEn;
        _allLeadsTitle.Text = file.Number is { } n ? $"{n} {title}" : title;

        // Reuse the editor monitor's calibration/speed/grid scheme, but force a static 12-lead grid
        // with no compare panes or pQRSt overlay — a plain read-only overview of every lead.
        _allLeadsMonitor.Mode = _monitorVm.MonitorMode with
        {
            Count = 12,
            SeriesScheme = SeriesScheme.Grid,
            LeadSelection = null,
            IsRunning = false,
            IsCompareMode = false,
            ShowImpulseLabels = false,
            ShowTips = true, // authoring preview always shows tips, regardless of the Teaching toggle
        };
        _allLeadsMonitor.Waveforms = BuildAllLeadsMap();
        _allLeadsMonitor.Tips = file.Tips;
        _allLeadsMonitor.TipComments = file.TipComments;
    }

    /// <summary>True while the read-only all-leads preview is on screen.</summary>
    private bool IsAllLeadsOverlayOpen => _allLeadsOverlay.Visibility == Visibility.Visible;

    /// <summary>Hides the read-only preview.</summary>
    private void CloseAllLeadsOverlay()
    {
        _allLeadsOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Builds a full 12-lead waveform map from the pathology being edited (so it reflects unsaved
    /// edits), baseline-zeroing each stored lead and synthesizing any missing derived leads exactly
    /// as the repository does — III/aVR/aVL/aVF from I+II, and V1/V3/V4/V5 from V2+V6.
    /// </summary>
    private IReadOnlyDictionary<Lead, Points> BuildAllLeadsMap()
    {
        var map = new Dictionary<Lead, Points>();
        var file = _editorVm?.TargetFile;
        if (file is null) return map;

        float[]? Zeroed(Lead l) =>
            file.Leads.TryGetValue(l, out var st)
                ? st.Samples.Select(v => (float)(v - _baseline)).ToArray()
                : null;

        foreach (var lead in Leads.All)
        {
            if (Zeroed(lead) is { } direct)
            {
                map[lead] = new Points(direct);
                continue;
            }

            IReadOnlyList<float>? synth = lead switch
            {
                Lead.III or Lead.aVR or Lead.aVL or Lead.aVF
                    when Zeroed(Lead.I) is { } i && Zeroed(Lead.II) is { } ii
                    => DerivedLeads.CombineIII_aVR_aVL_aVF(i, ii, lead),
                Lead.V1 or Lead.V3 or Lead.V4 or Lead.V5
                    when Zeroed(Lead.V2) is { } v2 && Zeroed(Lead.V6) is { } v6
                    => DerivedLeads.CombineV1_V3_V4_V5(v2, v6, lead),
                _ => null,
            };
            if (synth is { Count: > 0 }) map[lead] = new Points(synth);
        }

        // Apply the active display filter to every lead so the overview matches the looping preview
        // and the Teaching monitor (None → coefficients are null and the map is returned as-is).
        if (_monitorVm is { MonitorMode: var mode })
        {
            var coeffs = EcgDisplayFilter.Build(mode.FilterType, EcgDisplayFilter.SampleRate(mode));
            if (coeffs is { } c)
                foreach (var lead in map.Keys.ToList())
                    map[lead] = EcgDisplayFilter.Apply(map[lead], c.b, c.a);
        }
        return map;
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
                RequestedTheme = Theming.AppTheme.Current,
                Title = AppStrings.CtorError,
                Content = AppStrings.CtorLeadReadonly,
                CloseButtonText = AppStrings.CommonOk,
                XamlRoot = XamlRoot,
            };
            await warning.ShowAsync();
            return;
        }

        var hrSlider = new Slider { Header = AppStrings.CtorSynthHr, Minimum = 45, Maximum = 160, Value = 75, StepFrequency = 5 };
        var apSlider = new Slider { Header = AppStrings.CtorSynthAp, Minimum = -0.2, Maximum = 0.5, Value = 0.2, StepFrequency = 0.05 };
        var kpSlider = new Slider { Header = AppStrings.CtorSynthKp, Minimum = 10, Maximum = 100, Value = 80, StepFrequency = 5 };
        var arSlider = new Slider { Header = AppStrings.CtorSynthAr, Minimum = 0.5, Maximum = 2.0, Value = 1.0, StepFrequency = 0.1 };
        var krSlider = new Slider { Header = AppStrings.CtorSynthKr, Minimum = 10, Maximum = 150, Value = 40, StepFrequency = 5 };
        var asSlider = new Slider { Header = AppStrings.CtorSynthAs, Minimum = 0.0, Maximum = 1.0, Value = 0.2, StepFrequency = 0.05 };
        var ksSlider = new Slider { Header = AppStrings.CtorSynthKs, Minimum = 10, Maximum = 200, Value = 30, StepFrequency = 5 };
        var atSlider = new Slider { Header = AppStrings.CtorSynthAt, Minimum = -0.5, Maximum = 1.0, Value = 0.15, StepFrequency = 0.05 };
        var ktSlider = new Slider { Header = AppStrings.CtorSynthKt, Minimum = 50, Maximum = 300, Value = 220, StepFrequency = 10 };
        var varSlider = new Slider { Header = AppStrings.CtorSynthVar, Minimum = 0.0, Maximum = 0.15, Value = 0.01, StepFrequency = 0.01 };

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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorSynthTitle,
            Content = new ScrollViewer { Content = panel, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = AppStrings.QuickActionGenerate,
            CloseButtonText = AppStrings.CommonCancel,
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
                RequestedTheme = Theming.AppTheme.Current,
                Title = AppStrings.CtorSynthError,
                Content = AppStrings.CtorSynthErrorBody(ex.Message),
                CloseButtonText = AppStrings.CommonOk,
                XamlRoot = XamlRoot,
            };
            await errDialog.ShowAsync();
        }
    }

    private async void OnAutoDetectPoints(double? windowSeconds)
    {
        if (_editorVm?.TargetFile is null || _monitorVm is null) return;
        var lead = _editorVm.FocusedLead;
        var file = _editorVm.TargetFile;
        if (!file.Leads.TryGetValue(lead, out var stream)) return;

        try
        {
            double fs = _monitorVm.MonitorMode.Calibration.SampleRateHz;
            double[] sigDouble = stream.Samples.Select(x => (double)(x - _baseline)).ToArray();

            // Optionally limit detection to the leading window (1/3/5/10 s) chosen in the panel;
            // marker indices stay aligned since the slice starts at sample 0.
            if (windowSeconds is { } ws && fs > 0)
            {
                int n = (int)Math.Round(ws * fs);
                if (n > 0 && n < sigDouble.Length) sigDouble = sigDouble[..n];
            }

            int[] rpeaks = BioSPPy.Net.Signals.Ecg.QrsSegmenters.HamiltonSegmenter(sigDouble, fs);
            rpeaks = BioSPPy.Net.Signals.Ecg.QrsSegmenters.CorrectRPeaks(sigDouble, rpeaks, fs, 0.05);

            if (rpeaks.Length == 0)
            {
                var noPeaks = new ContentDialog
                {
                    RequestedTheme = Theming.AppTheme.Current,
                    Title = AppStrings.CtorAutoDetect,
                    Content = AppStrings.CtorAutoDetectNoPeaks,
                    CloseButtonText = AppStrings.CommonOk,
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
                RequestedTheme = Theming.AppTheme.Current,
                Title = AppStrings.CtorAutodetectError,
                Content = AppStrings.CtorAutodetectErrorBody(ex.Message),
                CloseButtonText = AppStrings.CommonOk,
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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorNewPathology,
            Content = panel,
            PrimaryButtonText = AppStrings.CourseCtorCreate,
            CloseButtonText = AppStrings.CommonCancel,
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
            await ShowError(AppStrings.CtorImportWfdb, AppStrings.CtorImportWfdbNoHea);
            return;
        }

        WfdbRecord record;
        try
        {
            record = await Task.Run(() => WfdbReader.ReadRecord(headerPath));
        }
        catch (Exception ex)
        {
            await ShowError(AppStrings.CtorImportWfdb, AppStrings.CtorImportWfdbReadFail(ex.Message));
            return;
        }

        await ImportRecordAsync(record, Path.GetFileNameWithoutExtension(headerPath));
    }

    private async void OnImportPhysioNetClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm is null) return;

        var pathBox = new TextBox
        {
            Header = AppStrings.CtorProjectPath,
            PlaceholderText = "challenge-2021/1.0.3/training/chapman_shaoxing/g1",
        };
        var recBox = new TextBox { Header = AppStrings.CtorRecord, PlaceholderText = "JS00001" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.CtorPhysioNetHelp,
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.6,
        });
        panel.Children.Add(pathBox);
        panel.Children.Add(recBox);
        panel.Children.Add(progress);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorPhysioNetTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CtorDownload,
            CloseButtonText = AppStrings.CommonCancel,
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
                status.Text = AppStrings.CtorPhysioNetNeedBoth;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                d.IsPrimaryButtonEnabled = false;
                progress.IsActive = true;
                status.Text = AppStrings.CtorDownloading;
                using var client = new PhysioNetClient();
                downloaded = await client.DownloadRecordAsync(path, rec);
                recordName = rec;
            }
            catch (Exception ex)
            {
                downloaded = null;
                args.Cancel = true;
                status.Text = AppStrings.CtorFailedFmt(ex.Message);
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
        var nameBox = new TextBox { Header = AppStrings.CtorName, Text = DeriveTitle(record, defaultName) };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.CtorImportSummary(record.ChannelCount, leadCount, record.SampleCount, record.Header.SamplingFrequency.ToString("0.#")),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        panel.Children.Add(nameBox);
        if (leadCount == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = AppStrings.CtorImportNoLeads,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.Red),
            });
        }

        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorImportTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CtorImport,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = leadCount > 0,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
        var file = WfdbConverter.ToPathologyFile(record, defaultName, title);
        var newId = _editorVm.ImportPathology(file);
        if (newId is null)
        {
            await ShowError(AppStrings.CtorImportFailed, AppStrings.CtorImportFailedBody);
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
            RequestedTheme = Theming.AppTheme.Current,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = AppStrings.CommonOk,
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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorDuplicateTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CtorDuplicate,
            CloseButtonText = AppStrings.CommonCancel,
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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorDeleteTitle,
            Content = AppStrings.CtorDeleteBody,
            PrimaryButtonText = AppStrings.CommonDelete,
            CloseButtonText = AppStrings.CommonCancel,
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
            Text = AppStrings.CtorDerivedBody,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorDerivedTitle,
            Content = body,
            PrimaryButtonText = AppStrings.QuickActionGenerate,
            CloseButtonText = AppStrings.CommonCancel,
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
                RequestedTheme = Theming.AppTheme.Current,
                Title = AppStrings.CtorInsertElement,
                Content = AppStrings.CtorInsertDerivedWarn,
                CloseButtonText = AppStrings.CommonOk,
                XamlRoot = XamlRoot,
            };
            await warn.ShowAsync();
            return;
        }

        var items = new (EcgElement Element, string Label)[]
        {
            (EcgElement.PWave, AppStrings.EditorPWave),
            (EcgElement.QrsComplex, AppStrings.EditorQrsComplex),
            (EcgElement.TWave, AppStrings.EditorTWave),
            (EcgElement.StSegment, AppStrings.CtorElementSt),
            (EcgElement.Baseline, AppStrings.CtorElementBaseline),
        };

        var combo = new ComboBox
        {
            Header = AppStrings.CtorElement,
            ItemsSource = items.Select(i => i.Label).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var widthBox = new NumberBox
        {
            Header = AppStrings.CtorWidthMs, Minimum = 1, SmallChange = 5, LargeChange = 20,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var heightBox = new NumberBox
        {
            Header = AppStrings.CtorHeightMv, SmallChange = 0.05, LargeChange = 0.2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        FieldFocus.SpinButtonsOnlyWhenFocused(widthBox);
        FieldFocus.SpinButtonsOnlyWhenFocused(heightBox);

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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorInsertAtCursor,
            Content = panel,
            PrimaryButtonText = AppStrings.SegInsert,
            CloseButtonText = AppStrings.CommonCancel,
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
                    Header = AppStrings.CtorWidthMs, Value = Math.Round(widthMs), Minimum = 1,
                    SmallChange = 5, LargeChange = 20, Width = 120,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                };
                var heightBox = new NumberBox
                {
                    Header = AppStrings.CtorHeightMv, Value = el.AmplitudeMv,
                    SmallChange = 0.05, LargeChange = 0.2, Width = 120,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                };
                widthBox.ValueChanged += (_, _) => Apply(idx, widthBox, heightBox);
                heightBox.ValueChanged += (_, _) => Apply(idx, widthBox, heightBox);
                FieldFocus.SpinButtonsOnlyWhenFocused(widthBox);
                FieldFocus.SpinButtonsOnlyWhenFocused(heightBox);
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
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorManageTitle(lead),
            Content = new ScrollViewer
            {
                Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = AppStrings.CommonClose,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ── Element palette ─────────────────────────────────────────────────────

    private void BuildPalette()
    {
        _palette.Children.Add(new TextBlock
        {
            Text = AppStrings.CtorPaletteInsert, VerticalAlignment = VerticalAlignment.Center,
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
            ToolTipService.SetToolTip(button, AppStrings.CtorPaletteInsertTip(ElementLabel(element)));
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
        EcgElement.PWave => AppStrings.EditorPWave,
        EcgElement.QrsComplex => "QRS",
        EcgElement.TWave => AppStrings.EditorTWave,
        EcgElement.StSegment => AppStrings.CtorElementSt,
        EcgElement.Baseline => AppStrings.CtorElementBaseline,
        _ => type.ToString(),
    };

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _appVm is null) return;
        var file = _editorVm.TargetFile;
        var lang = _appVm.SelectedLanguage;
        var currentName = lang == DomainLanguage.RU ? file.ResolvedNameRu ?? file.TitleEn : file.TitleEn;

        var input = new TextBox { Text = currentName, SelectionStart = currentName.Length };
        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.CtorRenameTitle,
            Content = input,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
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

        // Canonical taxonomy acronyms: every finding this rhythm exhibits, as removable chips. The first
        // is the primary diagnosis (used to auto-file an ungrouped rhythm's group).
        var picked = new List<string>(_editorVm.CurrentAcronyms);

        // Localized taxonomy name: Russian for RU, otherwise the English name (falling back to the
        // Russian name when the source didn't supply an English one). Used by the search filter, the
        // suggestion list, and the browse combo below so non-RU users don't see Russian-only labels.
        var ru = _appVm?.SelectedLanguage == DomainLanguage.RU;
        static string LocalizedName(TaxonomyEntry x, bool ru) =>
            ru ? x.NameRu : (string.IsNullOrWhiteSpace(x.NameEn) ? x.NameRu : x.NameEn);

        // Forward-declared so the chip-remove closures (created in RebuildAcronymChips, below) can
        // reach the browse combo through RefreshAcronyms; it's assigned its real value further down.
        ComboBox acronymBrowse = null!;
        var suppressBrowse = false;

        var acronymChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var acronymChipsScroll = new ScrollViewer
        {
            Content = acronymChips,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        };

        void RebuildAcronymChips()
        {
            acronymChips.Children.Clear();
            if (picked.Count == 0)
            {
                acronymChips.Children.Add(new TextBlock
                {
                    Text = AppStrings.TestCtorAcronymsNone,
                    FontSize = 12,
                    Foreground = AppTheme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return;
            }
            for (var i = 0; i < picked.Count; i++)
            {
                var code = picked[i];
                acronymChips.Children.Add(RhythmAcronymChip(code, primary: i == 0, () =>
                {
                    picked.RemoveAll(x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));
                    RefreshAcronyms(); // removed code returns to the browse list
                }));
            }
        }

        var acronymBox = new AutoSuggestBox
        {
            Header = AppStrings.TestCtorAcronyms,
            PlaceholderText = AppStrings.TestCtorAcronymsPlaceholder,
            QueryIcon = new SymbolIcon(Symbol.Tag),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        acronymBox.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var needle = acronymBox.Text.Trim();
            var chosen = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
            acronymBox.ItemsSource = Taxonomy.Shared.Entries
                .Where(x => !chosen.Contains(x.Acronym))
                .Where(x => needle.Length == 0
                    || x.Acronym.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || LocalizedName(x, ru).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .Select(x => $"{x.Acronym} — {LocalizedName(x, ru)}")
                .ToList();
        };
        acronymBox.QuerySubmitted += (_, args) =>
        {
            var token = (args.ChosenSuggestion as string) ?? args.QueryText;
            var code = Taxonomy.Normalize(token.Split('—')[0].Trim());
            if (code is not null && Taxonomy.Shared.Contains(code)
                && !picked.Any(p => string.Equals(p, code, StringComparison.OrdinalIgnoreCase)))
            {
                picked.Add(code);
                RefreshAcronyms();
            }
            acronymBox.Text = string.Empty;
        };

        // Browsable picker: the whole taxonomy in its natural (section → subsection) order, so the user
        // can scan and pick a code instead of having to know/guess one to type. Complements the
        // type-to-filter box above; already-picked codes are dropped from the list. Rebuilt on every
        // change so removed codes reappear and picked ones disappear.
        acronymBrowse = new ComboBox
        {
            PlaceholderText = AppStrings.TestCtorAcronymsBrowse,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 360,
        };
        void RebuildBrowseCombo()
        {
            suppressBrowse = true; // programmatic repopulation must not read back as a user pick
            acronymBrowse.Items.Clear();
            var chosen = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
            foreach (var x in Taxonomy.Shared.Entries.Where(x => !chosen.Contains(x.Acronym)))
                acronymBrowse.Items.Add(new ComboBoxItem { Content = $"{x.Acronym} — {LocalizedName(x, ru)}", Tag = x.Acronym });
            acronymBrowse.SelectedIndex = -1; // back to the placeholder
            suppressBrowse = false;
        }
        acronymBrowse.SelectionChanged += (_, _) =>
        {
            if (suppressBrowse) return;
            if (acronymBrowse.SelectedItem is ComboBoxItem item && item.Tag is string code
                && !picked.Any(p => string.Equals(p, code, StringComparison.OrdinalIgnoreCase)))
            {
                picked.Add(code);
                RefreshAcronyms();
            }
        };

        // Keeps the chip strip and the browse list in sync after any add/remove.
        void RefreshAcronyms()
        {
            RebuildAcronymChips();
            RebuildBrowseCombo();
        }
        RefreshAcronyms();

        var acronymSection = new StackPanel { Spacing = 6 };
        acronymSection.Children.Add(acronymBox);
        acronymSection.Children.Add(acronymBrowse);
        acronymSection.Children.Add(acronymChipsScroll);

        var panel = new StackPanel { Width = 360, Spacing = 12 };
        panel.Children.Add(acronymSection);
        panel.Children.Add(combo);
        panel.Children.Add(newGroupBox);

        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.GroupEditTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Acronyms first (the primary's group is the fallback below). Validation/dedup is inside SetAcronyms.
        _editorVm.SetAcronyms(picked);
        var primaryAcronym = picked.Count > 0 ? picked[0] : null;

        var newName = newGroupBox.Text?.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            var newKey = PathologyGroups.CreateGroup(newName);
            if (newKey is not null) _editorVm.SetGroup(newKey);
            return;
        }

        var idx = combo.SelectedIndex;
        if (idx >= 0 && idx < keys.Count)
        {
            var chosen = keys[idx];
            // Convenience: an ungrouped rhythm inherits its PRIMARY acronym's taxonomy group.
            if (chosen is null && Taxonomy.Shared.Find(primaryAcronym) is { } te) chosen = te.Group;
            _editorVm.SetGroup(chosen);
        }
    }

    /// <summary>A removable chip for one linked rhythm acronym; the primary (first) diagnosis is
    /// emphasized. Tooltip shows the code's localized name + subsection.</summary>
    private UIElement RhythmAcronymChip(string acronym, bool primary, Action onRemove)
    {
        var entry = Taxonomy.Shared.Find(acronym);
        var label = new TextBlock
        {
            Text = acronym,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (entry is not null)
        {
            var ru = _appVm?.SelectedLanguage == DomainLanguage.RU;
            var name = ru ? entry.NameRu : (string.IsNullOrWhiteSpace(entry.NameEn) ? entry.NameRu : entry.NameEn);
            ToolTipService.SetToolTip(label, $"{entry.Acronym} · {name} · §{entry.Subsection}");
        }

        var remove = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(5, 0, 5, 0),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => onRemove();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        row.Children.Add(remove);

        return new Border
        {
            Child = row,
            Background = primary ? AppTheme.AppAccentSoftBackground : AppTheme.AppSubtleFill,
            BorderBrush = AppTheme.Accent,
            BorderThickness = new Thickness(primary ? 2 : 1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 3, 4, 3),
        };
    }

    private async void OnClinicalCaseClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null) return;

        var currentClinicalCase = _editorVm.CurrentClinicalCase ?? string.Empty;
        
        string title = "";
        string description = "";
        string name = "";
        string age = "";
        string gender = "";
        string hr = "";
        string bp = "";
        var othersList = new List<string>();

        if (!string.IsNullOrWhiteSpace(currentClinicalCase))
        {
            var pairs = currentClinicalCase.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim().ToLowerInvariant();
                var val = parts[1].Trim();

                switch (key)
                {
                    case "title":
                    case "название":
                    case "título":
                    case "titulo":
                    case "标题":
                    case "शीर्षक":
                        title = val;
                        break;
                    case "description":
                    case "описание":
                    case "descripción":
                    case "descripcion":
                    case "描述":
                    case "विवरण":
                        description = val;
                        break;
                    case "name":
                    case "имя":
                    case "фио":
                    case "nombre":
                    case "姓名":
                    case "नाम":
                        name = val;
                        break;
                    case "age":
                    case "возраст":
                    case "edad":
                    case "年龄":
                    case "आयु":
                        age = val;
                        break;
                    case "gender":
                    case "пол":
                    case "género":
                    case "genero":
                    case "性别":
                    case "लिंग":
                        gender = val;
                        break;
                    case "hr":
                    case "heart_rate":
                    case "heartrate":
                    case "чсс":
                    case "frecuencia cardíaca":
                    case "frecuencia cardiaca":
                    case "心率":
                    case "हृदय दर":
                        hr = val;
                        break;
                    case "bp":
                    case "blood_pressure":
                    case "bloodpressure":
                    case "ад":
                    case "presión arterial":
                    case "presion arterial":
                    case "血压":
                    case "रक्तचाप":
                        bp = val;
                        break;
                    default:
                        othersList.Add($"{parts[0].Trim()}={val}");
                        break;
                }
            }
        }

        var titleBox = new TextBox { Header = AppStrings.ClinicalLabelTitle, Text = title, HorizontalAlignment = HorizontalAlignment.Stretch };
        var descriptionBox = new TextBox
        {
            Header = AppStrings.ClinicalLabelDescription,
            Text = description,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.Wrap,
        };
        var nameBox = new TextBox { Header = AppStrings.ClinicalLabelPatientName, Text = name, HorizontalAlignment = HorizontalAlignment.Stretch };
        var ageBox = new TextBox { Header = AppStrings.ClinicalLabelAge, Text = age, HorizontalAlignment = HorizontalAlignment.Stretch };
        ageBox.BeforeTextChanging += (sender, args) =>
        {
            if (args.NewText.Any(c => !char.IsDigit(c)))
            {
                args.Cancel = true;
            }
        };

        // Index 0 is an explicit "not specified" entry so gender can be cleared back to empty.
        // Without it, once a sex is picked the field can never be un-set, and any non-empty
        // clinical_case value turns a plain pathology into a clinical case.
        var genderOptions = new List<string> { AppStrings.GenderUnset, AppStrings.GenderMale, AppStrings.GenderFemale };
        int genderSelIdx = 0;
        if (!string.IsNullOrWhiteSpace(gender))
        {
            var gLower = gender.Trim().ToLowerInvariant();
            if (gLower == "male" || gLower == "мужской" || gLower == "мужчина" || gLower == "муж" || gLower == "masculino" || gLower == "hombre" || gLower == "男" || gLower == "男性" || gLower == "पुरुष")
            {
                genderSelIdx = 1;
            }
            else if (gLower == "female" || gLower == "женский" || gLower == "женщина" || gLower == "жен" || gLower == "femenino" || gLower == "mujer" || gLower == "女" || gLower == "女性" || gLower == "महिला")
            {
                genderSelIdx = 2;
            }
        }

        var genderBox = new ComboBox
        {
            Header = AppStrings.ClinicalLabelGender,
            ItemsSource = genderOptions,
            SelectedIndex = genderSelIdx,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var hrBox = new TextBox { Header = AppStrings.ClinicalLabelHr, Text = hr, HorizontalAlignment = HorizontalAlignment.Stretch };
        hrBox.BeforeTextChanging += (sender, args) =>
        {
            if (args.NewText.Any(c => !char.IsDigit(c)))
            {
                args.Cancel = true;
            }
        };
        var bpBox = new TextBox { Header = AppStrings.ClinicalLabelBp, Text = bp, HorizontalAlignment = HorizontalAlignment.Stretch };
        var othersBox = new TextBox 
        { 
            Header = AppStrings.ClinicalLabelOthers, 
            Text = string.Join(", ", othersList), 
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "temp=36.6, weight=70"
        };

        // Checking this empties every field at once, so a clinical case can be wiped back to a
        // plain pathology in one click instead of clearing each box by hand.
        var clearAllCheck = new CheckBox { Content = AppStrings.ClinicalClearAll };
        clearAllCheck.Checked += (_, _) =>
        {
            titleBox.Text = string.Empty;
            descriptionBox.Text = string.Empty;
            nameBox.Text = string.Empty;
            ageBox.Text = string.Empty;
            genderBox.SelectedIndex = 0;
            hrBox.Text = string.Empty;
            bpBox.Text = string.Empty;
            othersBox.Text = string.Empty;
        };

        var panel = new StackPanel { Width = 320, Spacing = 12 };
        panel.Children.Add(clearAllCheck);
        panel.Children.Add(titleBox);
        panel.Children.Add(descriptionBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(ageBox);
        panel.Children.Add(genderBox);
        panel.Children.Add(hrBox);
        panel.Children.Add(bpBox);
        panel.Children.Add(othersBox);

        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.ClinicalEditTitle,
            // Nine fields overflow the dialog's max height, clipping the last box ("Other parameters").
            // Wrap in a scroller (matching the other dialogs in this file) so overflow scrolls instead.
            Content = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newPairs = new List<string>();
        if (!string.IsNullOrWhiteSpace(titleBox.Text)) newPairs.Add($"title={titleBox.Text.Trim()}");
        // The clinical_case value is stored raw (no escaping) in a comma-delimited, single-line
        // header/manifest field, so strip separator chars to keep the record parseable.
        if (!string.IsNullOrWhiteSpace(descriptionBox.Text))
        {
            var descriptionValue = descriptionBox.Text.Trim()
                .Replace(',', ' ').Replace(';', ' ').Replace('\r', ' ').Replace('\n', ' ');
            if (!string.IsNullOrWhiteSpace(descriptionValue)) newPairs.Add($"description={descriptionValue}");
        }
        if (!string.IsNullOrWhiteSpace(nameBox.Text)) newPairs.Add($"name={nameBox.Text.Trim()}");
        if (!string.IsNullOrWhiteSpace(ageBox.Text)) newPairs.Add($"age={ageBox.Text.Trim()}");
        
        if (genderBox.SelectedIndex == 1) newPairs.Add("gender=Male");
        else if (genderBox.SelectedIndex == 2) newPairs.Add("gender=Female");

        if (!string.IsNullOrWhiteSpace(hrBox.Text)) newPairs.Add($"hr={hrBox.Text.Trim()}");
        if (!string.IsNullOrWhiteSpace(bpBox.Text)) newPairs.Add($"bp={bpBox.Text.Trim()}");

        if (!string.IsNullOrWhiteSpace(othersBox.Text))
        {
            var customPairs = othersBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var custom in customPairs)
            {
                var parts = custom.Split('=', 2);
                if (parts.Length == 2)
                {
                    newPairs.Add($"{parts[0].Trim()}={parts[1].Trim()}");
                }
            }
        }

        var resultString = newPairs.Count > 0 ? string.Join(",", newPairs) : null;
        _editorVm.SetClinicalCase(resultString);
    }

    private async void OnDescriptionClick(object sender, RoutedEventArgs e)
    {
        if (_editorVm?.TargetFile is null || _appVm is null) return;

        // ── Left: the raw HTML source the author types/pastes. Monospace + no spell-check/prediction
        //    (both fight markup and non-English medical text), matching the course "All in one" editor. ──
        var sourceBox = new TextBox
        {
            Text = _editorVm.CurrentDescription ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            PlaceholderText = AppStrings.DescriptionHtmlHint,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(sourceBox, ScrollBarVisibility.Auto);

        // ── Right: the same WebView2 renderer the course lectures use, so the author sees the
        //    description exactly as students will (app components, tables, KaTeX, <ecg> embeds). ──
        var preview = new LectureWebView();
        var resolveEcg = EcgTraceResolver.ForRepository(_appVm.Repository);
        void Render() => preview.SetLecture(DescriptionRendering.AsLecture(sourceBox.Text), resolveEcg);

        // Debounce keystrokes so the preview re-renders on a pause, not on every character.
        var debounce = DispatcherQueue.CreateTimer();
        debounce.IsRepeating = false;
        debounce.Interval = TimeSpan.FromMilliseconds(250);
        debounce.Tick += (_, _) => Render();
        sourceBox.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };

        var dialog = new ContentDialog
        {
            RequestedTheme = Theming.AppTheme.Current,
            Title = AppStrings.DescriptionEditTitle,
            Content = BuildDescriptionEditor(sourceBox, preview),
            PrimaryButtonText = AppStrings.CommonOk,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };
        // The stock ContentDialog is ~548px wide — too narrow for a side-by-side editor + preview.
        dialog.Resources["ContentDialogMaxWidth"] = 960d;

        Render(); // initial paint (stashed until the WebView finishes initializing)

        var result = await dialog.ShowAsync();
        debounce.Stop();
        if (result == ContentDialogResult.Primary)
            _editorVm.SetDescription(sourceBox.Text);
    }

    /// <summary>Two equal columns — the HTML source editor and its live rendered preview — each under a
    /// caption. Sized explicitly because a ContentDialog otherwise shrink-wraps to its content.</summary>
    private static Grid BuildDescriptionEditor(TextBox sourceBox, LectureWebView preview)
    {
        var grid = new Grid { Width = 860, Height = 460, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(LabeledPane(AppStrings.DescriptionHtmlSourceLabel, sourceBox, 0));

        var previewFrame = new Border
        {
            BorderBrush = AppTheme.ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = preview,
        };
        grid.Children.Add(LabeledPane(AppStrings.DescriptionHtmlPreviewLabel, previewFrame, 1));
        return grid;
    }

    /// <summary>Stacks a small caption over a stretched body element in the given dialog column.</summary>
    private static FrameworkElement LabeledPane(string caption, FrameworkElement body, int column)
    {
        var pane = new Grid { RowSpacing = 4 };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = caption,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.TextSecondary,
        };
        Grid.SetRow(label, 0);
        Grid.SetRow(body, 1);
        pane.Children.Add(label);
        pane.Children.Add(body);
        Grid.SetColumn(pane, column);
        return pane;
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    private void RefreshTabs()
    {
        // Remove only the per-lead buttons; leave _viewAllButton (a persistent field added once
        // at build time) parented. Re-parenting it via Clear()+Add() crashes WinUI (0xc000027b) —
        // this method runs on every tab switch, so that was the tab-switch crash.
        for (int i = _tabs.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_tabs.Children[i], _viewAllButton))
                _tabs.Children.RemoveAt(i);
        }
        if (_editorVm is null) return;

        // Insert lead buttons before the trailing _viewAllButton so it stays last in the row.
        int insertAt = 0;
        foreach (var lead in Leads.All)
        {
            var captured = lead;
            var isFocused = _editorVm.FocusedLead == lead;
            var isDirty = _editorVm.DirtyLeads.Contains(lead);
            var button = new Button
            {
                Content = lead.ToString(),
                Foreground = isDirty ? AppTheme.Negative : AppTheme.TextPrimary,
                FontWeight = isFocused ? FontWeights.Bold : FontWeights.Normal,
            };
            button.Click += (_, _) => _editorVm!.SelectLead(captured);
            _tabs.Children.Insert(insertAt++, button);
        }
    }
}
