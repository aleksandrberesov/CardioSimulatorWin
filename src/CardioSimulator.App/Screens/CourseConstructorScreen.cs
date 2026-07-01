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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    };
    private readonly LectureWebView _preview = new() { Margin = new Thickness(8) };
    private readonly HtmlBlockEditor _blockEditor = new() { Visibility = Visibility.Collapsed };
    private readonly Button _saveButton = new() { Content = AppStrings.CommonSave, Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = AppStrings.CourseCtorRevert, Visibility = Visibility.Collapsed };
    private readonly Button _newCourseButton = new() { Content = AppStrings.CourseCtorNewCourse };
    private readonly Button _deleteCourseButton = new() { Content = AppStrings.CourseCtorDeleteCourse, Visibility = Visibility.Collapsed };
    private readonly Button _newTopicButton = new() { Content = AppStrings.CourseCtorNewTopic };
    private readonly Button _deleteTopicButton = new() { Content = AppStrings.CourseCtorDeleteTopic, Visibility = Visibility.Collapsed };
    private readonly Button _newLectureButton = new() { Content = AppStrings.CourseCtorNewLecture };
    private readonly Button _renameLectureButton = new() { Content = AppStrings.CourseCtorRename, Visibility = Visibility.Collapsed };
    private readonly Button _deleteLectureButton = new() { Content = AppStrings.CourseCtorDeleteLecture, Visibility = Visibility.Collapsed };
    private readonly Button _modeToggle = new() { Content = AppStrings.CourseCtorModeVisual };
    private readonly Button _allInOneButton = new() { Content = AppStrings.CourseCtorAllInOne, Visibility = Visibility.Collapsed };
    private DispatcherQueueTimer? _previewDebounce;
    private bool _suppressEditorPush;
    private bool _blockMode;
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
    }

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
        // the editor (source / visual block) and the live preview, side by side.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(_htmlEditor, 0);
        body.Children.Add(_htmlEditor);

        Grid.SetColumn(_blockEditor, 0);
        body.Children.Add(_blockEditor);

        Grid.SetColumn(_preview, 1);
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

        // Bi-directional scroll sync between the visual block editor and the preview. A short
        // suppression window after a forward (editor→preview) scroll stops the preview's own
        // scroll report from echoing back and fighting the user.
        _blockEditor.BlockFocused += id =>
        {
            if (!_blockMode) return;
            _suppressReverseUntil = DateTime.UtcNow.AddMilliseconds(500);
            _preview.ScrollToBlock(id);
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
                LoadEditorFromVm();
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
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteCourse(course.Id);
    }

    private async Task ShowNewTopicDialogAsync()
    {
        if (_vm.SelectedCourse is null) return;
        var titleBox = new TextBox { Header = AppStrings.CourseCtorTitleHeader, PlaceholderText = AppStrings.CourseCtorTopicTitleHint, Width = 280, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var dialog = new ContentDialog
        {
            Title = AppStrings.CourseCtorNewTopic,
            Content = titleBox,
            PrimaryButtonText = AppStrings.CourseCtorCreate,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.CreateTopic(GenerateTopicId(title), title, null);
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
            choices.AddRange(course.Topics.Select(t => new TopicChoice(t.Id, CourseTopicFlyout.TopicName(t, IsRussian))));

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
