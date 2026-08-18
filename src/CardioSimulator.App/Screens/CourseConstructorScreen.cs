using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// CourseConstructor mode: side lists for courses + lectures, a raw HTML editor, and a live
/// WebView2 preview (KaTeX + ECG + editable quiz tables). Toolbar offers Save / Revert /
/// New lecture / Rename / Delete. Port of the Android <c>CourseConstructorScreen</c>.
/// </summary>
public sealed class CourseConstructorScreen : UserControl
{
    private readonly CourseConstructorViewModel _vm;
    private readonly AppViewModel _appVm;
    private readonly Func<Task<StorageFile?>>? _pickImage;

    private readonly TextBox _htmlEditor = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Margin = new Thickness(8),
        IsSpellCheckEnabled = false,
        // Visual (block) editing is the default, so the raw-source editor starts hidden.
        Visibility = Visibility.Collapsed,
    };
    private readonly LectureWebView _preview = new() { Margin = new Thickness(8) };
    private readonly HtmlBlockEditor _blockEditor = new();
    private readonly Button _saveButton = new() { Content = AppStrings.CommonSave, Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = AppStrings.CourseCtorRevert, Visibility = Visibility.Collapsed };
    private readonly Button _newCourseButton = new() { Content = AppStrings.CourseCtorNewCourse };
    private readonly Button _deleteCourseButton = new() { Content = AppStrings.CourseCtorDeleteCourse, Visibility = Visibility.Collapsed };
    private readonly Button _pathologiesButton = new() { Content = AppStrings.CourseCtorPathologies, Visibility = Visibility.Collapsed };
    private readonly Button _newTopicButton = new() { Content = AppStrings.CourseCtorNewTopic };
    private readonly Button _deleteTopicButton = new() { Content = AppStrings.CourseCtorDeleteTopic, Visibility = Visibility.Collapsed };
    private readonly Button _newLectureButton = new() { Content = AppStrings.CourseCtorNewLecture };
    private readonly Button _renameLectureButton = new() { Content = AppStrings.CourseCtorRename, Visibility = Visibility.Collapsed };
    private readonly Button _deleteLectureButton = new() { Content = AppStrings.CourseCtorDeleteLecture, Visibility = Visibility.Collapsed };
    // Visual (block) editing is the default; the toggle therefore offers a switch back to raw Source.
    private readonly Button _modeToggle = new() { Content = AppStrings.CourseCtorModeSource };
    private readonly Button _allInOneButton = new() { Content = AppStrings.CourseCtorAllInOne, Visibility = Visibility.Collapsed };
    private DispatcherQueueTimer? _previewDebounce;
    private bool _suppressEditorPush;
    private bool _blockMode = true; // Visual (block) editing is the default.
    private bool _suppressBlockReload;
    private DateTime _suppressReverseUntil;

    public CourseConstructorScreen(CourseConstructorViewModel vm, AppViewModel appVm, Func<Task<StorageFile?>>? pickImage = null)
    {
        _vm = vm;
        _appVm = appVm;
        _pickImage = pickImage;

        BuildLayout();
        WireEvents();
        // Course/lecture selection lives in the top bar and may already be set (it drives the shared
        // view-model before this screen is built), so seed the editor + preview from the current state.
        InitializeFromVm();

        // Emphasise Save as the primary action — it only appears when there are unsaved changes.
        _saveButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;

        // Prompt to save when leaving the constructor (mode switch) with unsaved edits.
        _appVm.LeaveGuardAsync = ConfirmLeaveAsync;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appVm.LeaveGuardAsync == ConfirmLeaveAsync) _appVm.LeaveGuardAsync = null;
    }

    /// <summary>The leave-guard body: prompt to save/discard when there are unsaved changes.</summary>
    private Task<bool> ConfirmLeaveAsync() => UnsavedChangesDialog.ConfirmAsync(XamlRoot, _vm);

    private void BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Padding = new Thickness(16, 8, 16, 8),
        };
        toolbar.Children.Add(_newCourseButton);
        toolbar.Children.Add(_deleteCourseButton);
        toolbar.Children.Add(_pathologiesButton);
        toolbar.Children.Add(_newTopicButton);
        toolbar.Children.Add(_deleteTopicButton);
        toolbar.Children.Add(_newLectureButton);
        toolbar.Children.Add(_renameLectureButton);
        toolbar.Children.Add(_deleteLectureButton);
        toolbar.Children.Add(_modeToggle);
        toolbar.Children.Add(_allInOneButton);
        toolbar.Children.Add(_saveButton);
        toolbar.Children.Add(_revertButton);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // Lectures + course are chosen from the app top bar now (like Teaching), so the body is just
        // the editor (source / visual block) and the live preview, side by side — with a draggable
        // splitter between them so the author can widen either pane.
        var body = new Grid();
        var leftCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var rightCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        body.ColumnDefinitions.Add(leftCol);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(rightCol);

        Grid.SetColumn(_htmlEditor, 0);
        body.Children.Add(_htmlEditor);

        Grid.SetColumn(_blockEditor, 0);
        body.Children.Add(_blockEditor);

        var splitter = BuildSplitter(body, leftCol, rightCol);
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);

        Grid.SetColumn(_preview, 2);
        body.Children.Add(_preview);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Content = root;
    }

    private void WireEvents()
    {
        _vm.PropertyChanged += OnVmChanged;

        _blockEditor.Initialize(_appVm, _appVm.Repository.Pathologies(), _pickImage);
        _appVm.Repository.ManifestChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(() => _blockEditor.SetRhythms(_appVm.Repository.Pathologies()));
        _blockEditor.HtmlChanged += OnBlockHtmlChanged;
        _modeToggle.Click += (_, _) => ToggleEditMode();

        // Click-to-edit: a click on a rendered block in the preview jumps the author straight to
        // editing that block (opens the ECG picker for an ECG, otherwise focuses its editor card).
        _preview.EnableEditClicks = true;
        _preview.EditElementRequested += OnPreviewEditRequested;

        // Bi-directional scroll sync between the visual block editor and the preview. A short
        // suppression window after a forward (editor→preview) scroll stops the preview's own
        // scroll report from echoing back and fighting the user.
        _blockEditor.BlockFocused += id =>
        {
            if (!_blockMode) return;
            _suppressReverseUntil = DateTime.UtcNow.AddMilliseconds(500);
            _preview.ScrollToBlock(id);
        };
        // Selecting a nested element in a Raw block's structure tree scrolls the preview to that element.
        _blockEditor.ElementSelected += (anchorId, indices) =>
        {
            if (!_blockMode) return;
            _suppressReverseUntil = DateTime.UtcNow.AddMilliseconds(500);
            _preview.ScrollToElement(anchorId, indices);
        };
        _preview.PreviewScrolled += id =>
        {
            if (!_blockMode || DateTime.UtcNow < _suppressReverseUntil) return;
            _blockEditor.ScrollToBlock(id);
        };

        _htmlEditor.TextChanged += (_, _) =>
        {
            if (_suppressEditorPush) return;
            _vm.SetHtml(_htmlEditor.Text);
            SchedulePreview();
        };

        _saveButton.Click += async (_, _) => await _vm.SaveAsync();
        _revertButton.Click += (_, _) => _vm.RevertLecture();
        _newCourseButton.Click += async (_, _) => await ShowNewCourseDialogAsync();
        _deleteCourseButton.Click += async (_, _) => await ShowDeleteCourseDialogAsync();
        _pathologiesButton.Click += async (_, _) => await ShowPathologiesDialogAsync();
        _newTopicButton.Click += async (_, _) => await ShowNewTopicDialogAsync();
        _deleteTopicButton.Click += async (_, _) => await ShowDeleteTopicDialogAsync();
        _newLectureButton.Click += async (_, _) => await ShowNewLectureDialogAsync();
        _allInOneButton.Click += async (_, _) => await ShowAllInOneDialogAsync();
        _renameLectureButton.Click += async (_, _) => await ShowRenameLectureDialogAsync();
        _deleteLectureButton.Click += async (_, _) => await ShowDeleteLectureDialogAsync();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CourseConstructorViewModel.SelectedCourse):
            case nameof(CourseConstructorViewModel.SelectedTopicId):
                // Course/topic/lecture selection is reflected in the top-bar selectors; the body only
                // needs to re-evaluate which authoring actions are available.
                UpdateToolbar();
                break;
            case nameof(CourseConstructorViewModel.TargetLecture):
                Data.ReloadDebug.Log($"Screen.OnVmChanged TargetLecture id={_vm.TargetLecture?.Id} rawLen={_vm.TargetLecture?.RawHtml.Length ?? -1} body='{Data.ReloadDebug.Snip(_vm.TargetLecture?.RawHtml)}'");
                LoadEditorFromVm();
                Data.ReloadDebug.Log($"  after LoadEditorFromVm editorText='{Data.ReloadDebug.Snip(_htmlEditor.Text)}' blockMode={_blockMode}");
                // Reload the visual editor only for external changes (lecture switch), not for
                // edits originating from the visual editor itself (which would steal focus).
                if (_blockMode && !_suppressBlockReload)
                    _blockEditor.LoadHtml(_vm.TargetLecture?.RawHtml ?? string.Empty);
                SchedulePreview();
                UpdateToolbar();
                break;
            case nameof(CourseConstructorViewModel.DirtyLectures):
            case nameof(CourseConstructorViewModel.IsMetadataDirty):
                UpdateToolbar();
                break;
        }
    }

    /// <summary>
    /// Seeds the editor + preview + toolbar from the current view-model state. The top-bar selectors
    /// may have already chosen a course/lecture before this screen instance existed, so its
    /// <see cref="OnVmChanged"/> handler (which only fires on subsequent changes) would otherwise miss
    /// the initial selection.
    /// </summary>
    private void InitializeFromVm()
    {
        LoadEditorFromVm();
        if (_blockMode) _blockEditor.LoadHtml(_vm.TargetLecture?.RawHtml ?? string.Empty);
        UpdateToolbar();
        if (_vm.TargetLecture is not null) SchedulePreview();
    }

    private void LoadEditorFromVm()
    {
        var text = _vm.TargetLecture?.RawHtml ?? string.Empty;
        // Skip self-originated updates (typing) so the caret doesn't jump on every keystroke.
        if (_htmlEditor.Text == text) return;
        _suppressEditorPush = true;
        try { _htmlEditor.Text = text; }
        finally { _suppressEditorPush = false; }
    }

    private void UpdateToolbar()
    {
        var hasLecture = _vm.TargetLecture is not null;
        var hasChanges = _vm.DirtyLectures.Count > 0 || _vm.IsMetadataDirty;
        _saveButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        _revertButton.Visibility = hasChanges && hasLecture ? Visibility.Visible : Visibility.Collapsed;
        var hasCourse = _vm.SelectedCourse is not null;
        _newLectureButton.IsEnabled = hasCourse;
        _newTopicButton.IsEnabled = hasCourse;
        _deleteCourseButton.Visibility = hasCourse ? Visibility.Visible : Visibility.Collapsed;
        _pathologiesButton.Visibility = hasCourse ? Visibility.Visible : Visibility.Collapsed;
        _deleteTopicButton.Visibility = _vm.SelectedTopicId is not null ? Visibility.Visible : Visibility.Collapsed;
        _renameLectureButton.Visibility = hasLecture ? Visibility.Visible : Visibility.Collapsed;
        _deleteLectureButton.Visibility = hasLecture ? Visibility.Visible : Visibility.Collapsed;
        _allInOneButton.Visibility = hasLecture ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBlockHtmlChanged(string html)
    {
        _suppressBlockReload = true;
        try { _vm.SetHtml(html); }
        finally { _suppressBlockReload = false; }

        _suppressEditorPush = true;
        try { _htmlEditor.Text = html; }
        finally { _suppressEditorPush = false; }

        SchedulePreview();
    }

    private void ToggleEditMode()
    {
        _blockMode = !_blockMode;
        if (_blockMode)
        {
            _blockEditor.LoadHtml(_vm.TargetLecture?.RawHtml ?? string.Empty);
            _blockEditor.Visibility = Visibility.Visible;
            _htmlEditor.Visibility = Visibility.Collapsed;
            _modeToggle.Content = AppStrings.CourseCtorModeSource;
        }
        else
        {
            _blockEditor.Visibility = Visibility.Collapsed;
            _htmlEditor.Visibility = Visibility.Visible;
            _modeToggle.Content = AppStrings.CourseCtorModeVisual;
        }
    }

    /// <summary>A preview click asks to edit a block: make sure the visual editor is showing, then open
    /// that block's editor (ECG picker, or scroll-and-focus its card).</summary>
    private void OnPreviewEditRequested(string elementId)
    {
        if (!_blockMode) ToggleEditMode(); // click-to-edit needs the visual block editor, not raw source
        _blockEditor.EditElementById(elementId);
    }

    // ── Editor / preview splitter ───────────────────────────────────────────────

    private const double MinPaneWidth = 200;

    /// <summary>
    /// A draggable handle between the editor and preview columns. Dragging freezes the left (editor)
    /// column to a pixel width and lets the right (preview) column, kept star-sized, fill the rest — so
    /// the author can widen either pane. Clamped so neither pane collapses below <see cref="MinPaneWidth"/>.
    /// </summary>
    private FrameworkElement BuildSplitter(Grid body, ColumnDefinition leftCol, ColumnDefinition rightCol)
    {
        // A thin visible grip centered in a wider transparent hit area.
        var grip = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 8),
            IsHitTestVisible = false,
        };
        var handle = new ResizeGrip
        {
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var dragging = false;
        var startPointerX = 0.0;
        var startLeftWidth = 0.0;

        handle.PointerPressed += (_, e) =>
        {
            var left = _blockMode ? _blockEditor.ActualWidth : _htmlEditor.ActualWidth;
            if (left <= 0) return;
            startPointerX = e.GetCurrentPoint(body).Position.X;
            startLeftWidth = left;
            // Freeze the left column to pixels for the drag; the right stays star so it fills the rest.
            leftCol.Width = new GridLength(left);
            rightCol.Width = new GridLength(1, GridUnitType.Star);
            dragging = handle.CapturePointer(e.Pointer);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var dx = e.GetCurrentPoint(body).Position.X - startPointerX;
            var max = body.ActualWidth - handle.Width - MinPaneWidth;
            leftCol.Width = new GridLength(Math.Clamp(startLeftWidth + dx, MinPaneWidth, Math.Max(MinPaneWidth, max)));
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCaptureLost += (_, _) => dragging = false;

        var host = new Grid();
        host.Children.Add(grip);
        host.Children.Add(handle);
        return host;
    }

    /// <summary>A thin drag handle that shows the horizontal-resize cursor. Based on
    /// <see cref="UserControl"/> because <see cref="Border"/> is sealed; a transparent inner border makes
    /// the whole width hit-testable.</summary>
    private sealed class ResizeGrip : UserControl
    {
        public ResizeGrip()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
            Content = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
    }

    private void SchedulePreview()
    {
        if (_previewDebounce is null)
        {
            _previewDebounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _previewDebounce.IsRepeating = false;
            _previewDebounce.Interval = TimeSpan.FromMilliseconds(200);
            _previewDebounce.Tick += (_, _) => RebuildPreview();
        }
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void RebuildPreview()
    {
        var lecture = _vm.TargetLecture;
        if (lecture is null) return;
        _preview.SetLecture(
            lecture,
            EcgTraceResolver.ForRepository(_appVm.Repository),
            _vm.Answers,
            _vm.SetTableCell);
    }

    private async Task ShowAllInOneDialogAsync()
    {
        if (_vm.TargetLecture is null) return;
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Height = 380,
            Width = 600,
            IsSpellCheckEnabled = false,
            PlaceholderText = AppStrings.CourseCtorAllInOneHint,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorAllInOneTitle,
            Content = box,
            PrimaryButtonText = AppStrings.CourseCtorImport,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var html = box.Text ?? string.Empty;
        if (html.Trim().Length == 0) return;
        _vm.ImportFullPage(html);
    }

    private async Task ShowNewCourseDialogAsync()
    {
        // Spell-check/auto-correct fight non-English (e.g. Russian) input with squiggles and
        // suggestion popups, so disable them on these short name/title fields.
        var titleBox = new TextBox { Header = AppStrings.CourseCtorCourseTitleHeader, PlaceholderText = AppStrings.CourseCtorCourseTitleHint, Width = 280, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorNewCourse,
            Content = titleBox,
            PrimaryButtonText = AppStrings.CourseCtorCreate,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.CreateCourse(GenerateCourseId(), title, null);
    }

    private static string GenerateCourseId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var buf = new char[16];
        for (var i = 0; i < buf.Length; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    private async Task ShowNewLectureDialogAsync()
    {
        if (_vm.SelectedCourse is null) return;
        // The subtopic id (its filename) is derived from the title automatically; the dialog asks for
        // the parent Тема (defaulting to the focused one) and the title.
        var topicCombo = BuildTopicCombo(_vm.SelectedTopicId);
        var titleBox = new TextBox { Header = AppStrings.CourseCtorTitleHeader, PlaceholderText = AppStrings.CourseCtorLectureTitleHint, Width = 280, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var stack = new StackPanel { Spacing = 8, Width = 280 };
        stack.Children.Add(topicCombo);
        stack.Children.Add(titleBox);
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorNewLecture,
            Content = stack,
            PrimaryButtonText = AppStrings.CourseCtorCreate,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.CreateLecture(GenerateLectureId(title), _appVm.SelectedLanguage.Tag(), title, null, ChosenTopicId(topicCombo));
    }

    /// <summary>
    /// Derives a unique slug id from a title, unique among <paramref name="existingIds"/> (a numeric
    /// suffix breaks ties). Titles with no usable ASCII characters (e.g. Cyrillic) fall back to a
    /// random id, like courses do.
    /// </summary>
    private static string UniqueSlug(string title, IEnumerable<string> existingIds)
    {
        var existing = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);

        var slug = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (ch < 128 && char.IsLetterOrDigit(ch)) slug.Append(ch);
            else if ((ch == ' ' || ch == '-' || ch == '_') && slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var baseId = slug.ToString().Trim('-');
        if (baseId.Length == 0) baseId = GenerateCourseId(); // no Latin chars — fall back to a random id

        var id = baseId;
        for (var n = 2; existing.Contains(id); n++) id = $"{baseId}-{n}";
        return id;
    }

    /// <summary>A subtopic id (its on-disk filename), unique within the course.</summary>
    private string GenerateLectureId(string title) =>
        UniqueSlug(title, _vm.SelectedCourse?.Lectures.Select(l => l.Id) ?? Enumerable.Empty<string>());

    /// <summary>A Тема id, unique among the course's topics.</summary>
    private string GenerateTopicId(string title) =>
        UniqueSlug(title, _vm.SelectedCourse?.Topics.Select(t => t.Id) ?? Enumerable.Empty<string>());

    private async Task ShowRenameLectureDialogAsync()
    {
        if (_vm.TargetLecture is null) return;
        // The Edit Подтема dialog also lets the user move the subtopic to a different Тема.
        var topicCombo = BuildTopicCombo(_vm.SelectedLecture?.Topic);
        var titleBox = new TextBox { Header = AppStrings.CourseCtorTitleHeader, Text = _vm.TargetLecture.FrontMatter.Title, Width = 280, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var stack = new StackPanel { Spacing = 8, Width = 280 };
        stack.Children.Add(topicCombo);
        stack.Children.Add(titleBox);
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorRenameLectureTitle,
            Content = stack,
            PrimaryButtonText = AppStrings.CourseCtorRename,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.RenameLecture(title, ChosenTopicId(topicCombo));
    }

    private async Task ShowDeleteLectureDialogAsync()
    {
        if (_vm.TargetLecture is null || _vm.SelectedCourse is null) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorDeleteLectureTitle,
            Content = AppStrings.CourseCtorDeleteLectureBody(_vm.TargetLecture.FrontMatter.Title),
            PrimaryButtonText = AppStrings.CourseCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteLecture(_vm.TargetLecture.Id, _vm.TargetLecture.Language);
    }

    private async Task ShowDeleteCourseDialogAsync()
    {
        if (_vm.SelectedCourse is not { } course) return;
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorDeleteCourseTitle,
            Content = AppStrings.CourseCtorDeleteCourseBody(course.TitleEn),
            PrimaryButtonText = AppStrings.CourseCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteCourse(course.Id);
    }

    /// <summary>
    /// Explicit rhythm-list picker (option 2): a searchable checklist of every pathology, letting the
    /// author choose which rhythms the course's Teaching monitor drawer shows. Rhythms already embedded in
    /// a lecture (<c>&lt;ecg&gt;</c>/<c>&lt;ecgsegment&gt;</c>) are pre-checked and locked — they are always
    /// included (see <see cref="CourseConstructorViewModel.SaveAsync"/>'s auto-derive), so the picker only
    /// governs the <em>extra</em> rhythms the author wants selectable without embedding them in a lecture.
    /// </summary>
    private async Task ShowPathologiesDialogAsync()
    {
        if (_vm.SelectedCourse is not { } course) return;

        var allRhythms = _appVm.Repository.Pathologies();
        // Reading every lecture body can touch disk, so resolve the embedded (locked) set off the UI thread.
        // Failure here must not block the picker — fall back to no locked rows so the author can still edit.
        HashSet<string> embedded;
        try
        {
            embedded = new HashSet<string>(
                await Task.Run(() => _vm.DeriveEmbeddedPathologies(course)), StringComparer.Ordinal);
        }
        catch
        {
            embedded = new HashSet<string>(StringComparer.Ordinal);
        }
        var selected = new HashSet<string>(course.Pathologies, StringComparer.Ordinal);

        var search = new TextBox
        {
            PlaceholderText = AppStrings.RhythmSearchPlaceholder,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        var list = new StackPanel { Spacing = 2 };
        var rows = new List<(string Id, CheckBox Box)>();

        foreach (var p in allRhythms.OrderBy(PathologyTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            var isEmbedded = embedded.Contains(p.Id);
            var box = new CheckBox
            {
                Content = isEmbedded ? $"{PathologyTitle(p)}  ·  {AppStrings.CourseCtorPathologiesEmbedded}" : PathologyTitle(p),
                IsChecked = isEmbedded || selected.Contains(p.Id),
                IsEnabled = !isEmbedded, // embedded rhythms are always included and can't be unchecked
                Tag = p.Id,
                MinWidth = 0,
            };
            rows.Add((p.Id, box));
            list.Children.Add(box);
        }

        search.TextChanged += (_, _) =>
        {
            var query = (search.Text ?? string.Empty).Trim();
            foreach (var (id, box) in rows)
            {
                var p = allRhythms.FirstOrDefault(r => r.Id == id);
                var matches = query.Length == 0 || PathologyMatches(p, query);
                box.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            }
        };

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 380,
        };
        var body = new StackPanel { Spacing = 8, Width = 380 };
        body.Children.Add(new TextBlock
        {
            Text = AppStrings.CourseCtorPathologiesSubtitle,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });
        body.Children.Add(search);
        body.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorPathologiesTitle,
            Content = body,
            PrimaryButtonText = AppStrings.CommonSave,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var chosen = rows.Where(r => r.Box.IsChecked == true).Select(r => r.Id).ToList();
        _vm.SetCoursePathologies(chosen);
    }

    /// <summary>The pathology's display title in the active language (RU name when Russian, else English),
    /// prefixed with its catalogue number when it has one — matching the rhythm drawer's row label.</summary>
    private string PathologyTitle(PathologyEntry p)
    {
        var title = _appVm.SelectedLanguage == DomainLanguage.RU ? (p.NameRu ?? p.TitleEn) : p.TitleEn;
        return p.Number is { } n ? $"{n} {title}" : title;
    }

    /// <summary>Whether a pathology matches the picker's search query: title substring, or (for a numeric
    /// query) its catalogue number by prefix — mirroring <see cref="RhythmChoosingPanel"/>'s search.</summary>
    private bool PathologyMatches(PathologyEntry? p, string query)
    {
        if (p is null) return false;
        if (PathologyTitle(p).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Number is { } number && query.All(char.IsDigit))
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                .StartsWith(query, StringComparison.Ordinal);
        return false;
    }

    private async Task ShowNewTopicDialogAsync()
    {
        if (_vm.SelectedCourse is null) return;
        var titleBox = new TextBox { Header = AppStrings.CourseCtorTitleHeader, PlaceholderText = AppStrings.CourseCtorTopicTitleHint, Width = 280, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };

        // The author decides the Тема's shape here: a group that holds Подтемы (Course → Тема → Подтема)
        // or a content-bearing leaf that is itself a lecture (Course → Тема).
        var kind = new RadioButtons { Header = AppStrings.CourseCtorTopicKindHeader };
        kind.Items.Add(AppStrings.CourseCtorTopicKindGroup);
        kind.Items.Add(AppStrings.CourseCtorTopicKindLeaf);
        kind.SelectedIndex = 0;

        var stack = new StackPanel { Spacing = 12, Width = 280 };
        stack.Children.Add(titleBox);
        stack.Children.Add(kind);
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorNewTopic,
            Content = stack,
            PrimaryButtonText = AppStrings.CourseCtorCreate,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        var isLeaf = kind.SelectedIndex == 1;
        _vm.CreateTopic(GenerateTopicId(title), title, null, isLeaf, _appVm.SelectedLanguage.Tag());
    }

    private async Task ShowDeleteTopicDialogAsync()
    {
        if (_vm.SelectedCourse is not { } course || _vm.SelectedTopicId is not { } topicId) return;
        var topic = course.Topics.FirstOrDefault(t => t.Id == topicId);
        var name = topic is null ? topicId : CourseTopicFlyout.TopicName(topic, IsRussian);
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorDeleteTopicTitle,
            Content = AppStrings.CourseCtorDeleteTopicBody(name),
            PrimaryButtonText = AppStrings.CourseCtorDelete,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteTopic(topicId, _appVm.SelectedLanguage.Tag());
    }

    private bool IsRussian => _appVm.SelectedLanguage == DomainLanguage.RU;

    /// <summary>A Тема dropdown for the subtopic dialogs: a "(no topic)" entry first, then the
    /// course's topics; pre-selects <paramref name="selectedId"/>.</summary>
    private ComboBox BuildTopicCombo(string? selectedId)
    {
        var choices = new List<TopicChoice> { new(null, AppStrings.CourseCtorNoTopic) };
        if (_vm.SelectedCourse is { } course)
            // Only group Темы can parent a Подтема; a leaf Тема is itself a lecture, not a container.
            choices.AddRange(course.Topics.Where(t => !t.IsLeaf)
                .Select(t => new TopicChoice(t.Id, CourseTopicFlyout.TopicName(t, IsRussian))));

        var combo = new ComboBox { Header = AppStrings.TopicSelectorTitle, Width = 280, ItemsSource = choices };
        combo.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedId) ?? choices[0];
        return combo;
    }

    private static string? ChosenTopicId(ComboBox combo) => (combo.SelectedItem as TopicChoice)?.Id;

    private sealed record TopicChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }
}
