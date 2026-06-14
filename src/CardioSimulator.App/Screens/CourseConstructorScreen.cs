using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Rendering;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
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

    private readonly ComboBox _courseSelector = new() { MinWidth = 220, PlaceholderText = "Course" };
    private readonly ListView _lectureList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240 };
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
    private readonly Button _saveButton = new() { Content = "Save", Visibility = Visibility.Collapsed };
    private readonly Button _revertButton = new() { Content = "Revert", Visibility = Visibility.Collapsed };
    private readonly Button _newCourseButton = new() { Content = "New Course" };
    private readonly Button _newLectureButton = new() { Content = "New Lecture" };
    private readonly Button _renameLectureButton = new() { Content = "Rename", Visibility = Visibility.Collapsed };
    private readonly Button _deleteLectureButton = new() { Content = "Delete Lecture", Visibility = Visibility.Collapsed };
    private readonly Button _modeToggle = new() { Content = "Visual" };
    private DispatcherQueueTimer? _previewDebounce;
    private bool _suppressEditorPush;
    private bool _blockMode;
    private bool _suppressBlockReload;
    private bool _suppressLectureSelection;
    private bool _suppressCourseSelection;
    private DateTime _suppressReverseUntil;

    public CourseConstructorScreen(CourseConstructorViewModel vm, AppViewModel appVm, Func<Task<StorageFile?>>? pickImage = null)
    {
        _vm = vm;
        _appVm = appVm;
        _pickImage = pickImage;

        BuildLayout();
        WireEvents();
        RefreshCourses();
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
        toolbar.Children.Add(_courseSelector);
        toolbar.Children.Add(_newCourseButton);
        toolbar.Children.Add(_newLectureButton);
        toolbar.Children.Add(_renameLectureButton);
        toolbar.Children.Add(_deleteLectureButton);
        toolbar.Children.Add(_modeToggle);
        toolbar.Children.Add(_saveButton);
        toolbar.Children.Add(_revertButton);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var nav = new StackPanel { Padding = new Thickness(12), Spacing = 8 };
        nav.Children.Add(new TextBlock { Text = "Lectures", FontWeight = FontWeights.SemiBold });
        nav.Children.Add(_lectureList);
        Grid.SetColumn(nav, 0);
        body.Children.Add(nav);

        Grid.SetColumn(_htmlEditor, 1);
        body.Children.Add(_htmlEditor);

        Grid.SetColumn(_blockEditor, 1);
        body.Children.Add(_blockEditor);

        Grid.SetColumn(_preview, 2);
        body.Children.Add(_preview);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Content = root;
    }

    private void WireEvents()
    {
        _vm.PropertyChanged += OnVmChanged;
        _vm.Repository.ManifestChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshCourses);

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

        _courseSelector.SelectionChanged += (_, _) =>
        {
            if (_suppressCourseSelection) return;
            if (_courseSelector.SelectedItem is CourseRowItem item && item.Id != _vm.SelectedCourse?.Id)
                _vm.SelectCourse(item.Id);
        };
        _lectureList.SelectionChanged += (_, _) =>
        {
            if (_suppressLectureSelection) return;
            if (_lectureList.SelectedItem is LectureRowItem item && _vm.SelectedCourse is not null
                && item.Id != _vm.SelectedLecture?.Id)
            {
                var langTag = _appVm.SelectedLanguage.Tag();
                _vm.SelectLecture(item.Id, langTag);
            }
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
        _newLectureButton.Click += async (_, _) => await ShowNewLectureDialogAsync();
        _renameLectureButton.Click += async (_, _) => await ShowRenameLectureDialogAsync();
        _deleteLectureButton.Click += async (_, _) => await ShowDeleteLectureDialogAsync();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CourseConstructorViewModel.SelectedCourse):
                RefreshLectures();
                UpdateToolbar();
                break;
            case nameof(CourseConstructorViewModel.SelectedLecture):
                SyncLectureSelection();
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

    private void RefreshCourses()
    {
        string Label(string id, string titleEn, string? nameRu) =>
            _appVm.SelectedLanguage == DomainLanguage.RU ? (nameRu ?? titleEn) : titleEn;

        var items = _vm.Repository.Courses
            .Select(c => new CourseRowItem(c.Id, Label(c.Id, c.TitleEn, c.NameRu)))
            .ToList();

        // Keep a just-created (not-yet-saved) course selectable until the manifest reload picks it up.
        if (_vm.SelectedCourse is { } sel && items.All(i => i.Id != sel.Id))
            items.Add(new CourseRowItem(sel.Id, Label(sel.Id, sel.TitleEn, sel.NameRu)));

        var prevSel = (_courseSelector.SelectedItem as CourseRowItem)?.Id ?? _vm.SelectedCourse?.Id;
        _suppressCourseSelection = true;
        try
        {
            _courseSelector.ItemsSource = items;
            if (prevSel is not null)
                _courseSelector.SelectedItem = items.FirstOrDefault(i => i.Id == prevSel);
        }
        finally
        {
            _suppressCourseSelection = false;
        }
    }

    private void RefreshLectures()
    {
        var lectures = _vm.SelectedCourse?.Lectures ?? new List<LectureEntry>();
        var items = lectures
            .Select(l => new LectureRowItem(l.Id, _appVm.SelectedLanguage == DomainLanguage.RU ? (l.NameRu ?? l.TitleEn) : l.TitleEn))
            .ToList();
        _suppressLectureSelection = true;
        try { _lectureList.ItemsSource = items; }
        finally { _suppressLectureSelection = false; }
        SyncLectureSelection();
    }

    private void SyncLectureSelection()
    {
        var targetId = _vm.SelectedLecture?.Id;
        var items = _lectureList.ItemsSource as System.Collections.Generic.List<LectureRowItem>;
        var item = targetId is not null ? items?.FirstOrDefault(i => i.Id == targetId) : null;
        _suppressLectureSelection = true;
        try { _lectureList.SelectedItem = item; }
        finally { _suppressLectureSelection = false; }
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
        _newLectureButton.IsEnabled = _vm.SelectedCourse is not null;
        _renameLectureButton.Visibility = hasLecture ? Visibility.Visible : Visibility.Collapsed;
        _deleteLectureButton.Visibility = hasLecture ? Visibility.Visible : Visibility.Collapsed;
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
            _modeToggle.Content = "Source";
        }
        else
        {
            _blockEditor.Visibility = Visibility.Collapsed;
            _htmlEditor.Visibility = Visibility.Visible;
            _modeToggle.Content = "Visual";
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

    private async Task ShowNewCourseDialogAsync()
    {
        var titleBox = new TextBox { Header = "Course title", PlaceholderText = "e.g. ECG basics", Width = 280 };
        var dialog = new ContentDialog
        {
            Title = "New Course",
            Content = titleBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.CreateCourse(GenerateCourseId(), title, null);
        RefreshCourses();
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
        var idBox = new TextBox { Header = "Lecture id", PlaceholderText = "e.g. intro" };
        var titleBox = new TextBox { Header = "Title (English)", PlaceholderText = "e.g. Introduction" };
        var stack = new StackPanel { Spacing = 8, Width = 280 };
        stack.Children.Add(idBox);
        stack.Children.Add(titleBox);
        var dialog = new ContentDialog
        {
            Title = "New Lecture",
            Content = stack,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var id = (idBox.Text ?? string.Empty).Trim();
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (id.Length == 0 || title.Length == 0) return;
        _vm.CreateLecture(id, _appVm.SelectedLanguage.Tag(), title, null);
    }

    private async Task ShowRenameLectureDialogAsync()
    {
        if (_vm.TargetLecture is null) return;
        var titleBox = new TextBox { Header = "Title", Text = _vm.TargetLecture.FrontMatter.Title, Width = 280 };
        var dialog = new ContentDialog
        {
            Title = "Rename Lecture",
            Content = titleBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var title = (titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0) return;
        _vm.RenameLecture(title);
    }

    private async Task ShowDeleteLectureDialogAsync()
    {
        if (_vm.TargetLecture is null || _vm.SelectedCourse is null) return;
        var dialog = new ContentDialog
        {
            Title = "Delete lecture?",
            Content = $"Permanently delete \"{_vm.TargetLecture.FrontMatter.Title}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteLecture(_vm.TargetLecture.Id, _vm.TargetLecture.Language);
    }

    private sealed record CourseRowItem(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private sealed record LectureRowItem(string Id, string Title)
    {
        public override string ToString() => Title;
    }
}
