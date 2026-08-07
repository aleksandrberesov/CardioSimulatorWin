using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Authoring view-model for the HTML course constructor. Holds the selected course/lecture,
/// the raw-HTML draft, and the per-lecture quiz answers (<c>.answers.json</c>). Port of the
/// Android <c>CourseConstructorViewModel</c> (HTML body + answers).
/// </summary>
public partial class CourseConstructorViewModel : ObservableObject
{
    private readonly CourseRepository _repository;
    private readonly HashSet<string> _dirtyLectures = new();
    private Dictionary<string, Dictionary<string, string>> _answers = new();
    private bool _answersDirty;

    [ObservableProperty]
    private Course? _selectedCourse;

    [ObservableProperty]
    private LectureEntry? _selectedLecture;

    /// <summary>
    /// The Тема (topic) currently in focus for authoring — the topic of the selected Подтема, or the
    /// last topic created/opened. Used as the default target when adding/moving a subtopic and as the
    /// subject of topic rename/delete.
    /// </summary>
    [ObservableProperty]
    private string? _selectedTopicId;

    [ObservableProperty]
    private Lecture? _targetLecture;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isMetadataDirty;

    public CourseConstructorViewModel(CourseRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Exposes the backing repository so the screen can list courses + read lectures.</summary>
    public CourseRepository Repository => _repository;

    public IReadOnlyCollection<string> DirtyLectures => _dirtyLectures.ToArray();

    /// <summary>True when there is unsaved work — an edited lecture or a pending structural (metadata) change.</summary>
    public bool HasUnsavedChanges => _dirtyLectures.Count > 0 || IsMetadataDirty;

    /// <summary>Saved quiz answers for the current lecture (quizId → "row,col" → value).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Answers =>
        _answers.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);

    public void SelectCourse(string id)
    {
        SelectedCourse = _repository.ReadCourse(id);
        SelectedLecture = null;
        SelectedTopicId = null;
        TargetLecture = null;
        _answers = new();
        _answersDirty = false;
        _dirtyLectures.Clear();
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
        IsMetadataDirty = false;
    }

    public void SelectLecture(string lectureId, string language)
    {
        if (SelectedCourse is null) return;
        // ContentItem resolves a real Подтема OR a leaf Тема (its content is keyed by the topic id).
        SelectedLecture = SelectedCourse.ContentItem(lectureId);
        // Keep the focused Тема in sync so subtopic add/move and topic delete default to the right
        // topic. A leaf Тема's synthesized entry points its Topic at itself, so it focuses the Тема.
        if (SelectedLecture?.Topic is { } topic) SelectedTopicId = topic;

        var loaded = _repository.ReadLecture(SelectedCourse.Id, lectureId, language);
        if (loaded is null && _dirtyLectures.Contains(lectureId) && TargetLecture?.Id == lectureId)
            return; // lecture exists only in memory (not yet saved) — keep the in-memory draft

        TargetLecture = loaded;
        var lang = TargetLecture?.Language ?? language;
        _answers = ParseAnswers(_repository.ReadAnswers(SelectedCourse.Id, lectureId, lang));
        _answersDirty = false;
        OnPropertyChanged(nameof(Answers));
    }

    public void SetHtml(string text)
    {
        if (TargetLecture is null) return;
        // Keep the "layout: standalone" flag in step with the content: a full document stays flagged; once it
        // becomes a fragment (e.g. decomposed to combine with app components), the flag is cleared so it never
        // goes stale. Rendering is content-driven regardless (HtmlCompiler.IsFullDocument).
        TargetLecture = (TargetLecture with { RawHtml = text }).WithReconciledLayout();
        _dirtyLectures.Add(TargetLecture.Id);
        OnPropertyChanged(nameof(DirtyLectures));
    }

    /// <summary>
    /// Replaces the current lecture's body with a pasted HTML document ("All in one"). A complete page
    /// (<c>&lt;!doctype</c>/<c>&lt;html</c>) is stored verbatim and flagged <c>layout: standalone</c>; a body
    /// fragment clears the flag. Both are handled by <see cref="SetHtml"/>'s layout reconciliation.
    /// </summary>
    public void ImportFullPage(string html) => SetHtml(html);

    /// <summary>Appends an HTML snippet to the draft (toolbar insert actions).</summary>
    public void InsertSnippet(string html)
    {
        if (TargetLecture is null) return;
        var body = TargetLecture.RawHtml;
        var joined = body.Length == 0 ? html : body.TrimEnd('\n') + "\n" + html + "\n";
        SetHtml(joined);
    }

    public void SetFrontMatter(LectureFrontMatter frontMatter)
    {
        if (TargetLecture is null) return;
        TargetLecture = TargetLecture with { FrontMatter = frontMatter };
        _dirtyLectures.Add(TargetLecture.Id);
        OnPropertyChanged(nameof(DirtyLectures));
    }

    /// <summary>
    /// Renames the current content item and (for a Подтема) optionally moves it to a different Тема.
    /// <paramref name="topicId"/> is the destination topic id (null = ungrouped); pass the current topic
    /// to leave it in place. When the open item is a <b>leaf Тема</b>, this renames the Тема itself
    /// (it has no <see cref="LectureEntry"/> and can't be re-parented, so <paramref name="topicId"/> is
    /// ignored).
    /// </summary>
    public void RenameLecture(string newTitle, string? topicId)
    {
        if (SelectedCourse is null || TargetLecture is null) return;
        var id = TargetLecture.Id;
        var fm = TargetLecture.FrontMatter with { Title = newTitle };
        TargetLecture = TargetLecture with { FrontMatter = fm };

        var leaf = SelectedCourse.Topics.FirstOrDefault(t => t.IsLeaf && t.Id == id);
        if (leaf is not null)
        {
            var topics = SelectedCourse.Topics
                .Select(t => t.Id == id ? t with { TitleEn = newTitle } : t)
                .ToList();
            SelectedCourse = SelectedCourse with { Topics = topics };
            SelectedLecture = new LectureEntry(id, newTitle, leaf.NameRu, id);
            SelectedTopicId = id;
        }
        else
        {
            var lectures = SelectedCourse.Lectures
                .Select(l => l.Id == id ? l with { TitleEn = newTitle, Topic = topicId } : l)
                .ToList();
            SelectedCourse = SelectedCourse with { Lectures = lectures };
            SelectedLecture = lectures.FirstOrDefault(l => l.Id == id);
            SelectedTopicId = topicId;
        }
        _dirtyLectures.Add(id);
        IsMetadataDirty = true;
        OnPropertyChanged(nameof(DirtyLectures));
    }

    public void SetTableCell(string quizId, int row, int col, string value)
    {
        if (!_answers.TryGetValue(quizId, out var cells))
        {
            cells = new Dictionary<string, string>();
            _answers[quizId] = cells;
        }
        cells[$"{row},{col}"] = value;
        _answersDirty = true;
        OnPropertyChanged(nameof(Answers));
    }

    /// <summary>
    /// Reverts ALL unsaved work to the last-saved state: reloads the course (dropping unsaved topics,
    /// subtopics and renames) and the open lecture from disk, and clears every dirty flag. Backs the
    /// "Don't save" choice of the unsaved-changes prompt.
    /// </summary>
    public void DiscardChanges()
    {
        if (SelectedCourse is null) return;
        var courseId = SelectedCourse.Id;
        var reloaded = _repository.ReadCourse(courseId);

        _dirtyLectures.Clear();
        _answersDirty = false;
        IsMetadataDirty = false;

        if (reloaded is null)
        {
            // A brand-new course never saved to disk — nothing to revert to; just drop the dirty flags.
            OnPropertyChanged(nameof(DirtyLectures));
            return;
        }
        SelectedCourse = reloaded;

        // Re-open the current item from disk when it still exists there, else clear the editor.
        var openId = TargetLecture?.Id ?? SelectedLecture?.Id;
        var lang = TargetLecture?.Language ?? "en";
        if (openId is not null && reloaded.ContentItem(openId) is { } item)
        {
            SelectedLecture = item;
            TargetLecture = _repository.ReadLecture(courseId, openId, lang);
            _answers = ParseAnswers(_repository.ReadAnswers(courseId, openId, lang));
        }
        else
        {
            SelectedLecture = null;
            TargetLecture = null;
            _answers = new();
        }
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    public void RevertLecture()
    {
        if (SelectedCourse is null || TargetLecture is null) return;
        var id = TargetLecture.Id;
        var lang = TargetLecture.Language;
        TargetLecture = _repository.ReadLecture(SelectedCourse.Id, id, lang);
        _answers = ParseAnswers(_repository.ReadAnswers(SelectedCourse.Id, id, lang));
        _answersDirty = false;
        _dirtyLectures.Remove(id);
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    /// <summary>Creates a new Подтема (leaf) under the given Тема (<paramref name="topicId"/>, null = ungrouped).</summary>
    public void CreateLecture(string id, string language, string titleEn, string? nameRu, string? topicId = null)
    {
        if (SelectedCourse is null) return;

        var fm = new LectureFrontMatter(id, 0, titleEn, 1, new Dictionary<string, string>());
        TargetLecture = new Lecture(id, SelectedCourse.Id, language, fm, string.Empty);

        var lectures = SelectedCourse.Lectures.ToList();
        lectures.Add(new LectureEntry(id, titleEn, nameRu, topicId));
        SelectedCourse = SelectedCourse with { Lectures = lectures };
        SelectedLecture = lectures[^1];
        SelectedTopicId = topicId;

        _answers = new();
        _answersDirty = false;
        _dirtyLectures.Add(id);
        IsMetadataDirty = true;
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    /// <summary>
    /// Creates a Тема and focuses it. A <b>group</b> Тема (<paramref name="isLeaf"/> = false) is empty
    /// so Подтемы can be added under it. A <b>leaf</b> Тема (<paramref name="isLeaf"/> = true) is
    /// content-bearing (Course → Тема): a blank lecture draft (<c>lectures/&lt;id&gt;.&lt;lang&gt;.html</c>,
    /// keyed by the topic id) is opened for authoring, so <paramref name="language"/> is required for it.
    /// </summary>
    public void CreateTopic(string id, string titleEn, string? nameRu, bool isLeaf = false, string language = "en")
    {
        if (SelectedCourse is null) return;
        var topics = SelectedCourse.Topics.ToList();
        topics.Add(new TopicEntry(id, titleEn, nameRu, isLeaf));
        SelectedCourse = SelectedCourse with { Topics = topics };
        SelectedTopicId = id;
        IsMetadataDirty = true;

        if (!isLeaf) return; // group Тема has no content of its own — keep the current draft

        var fm = new LectureFrontMatter(id, 0, titleEn, 1, new Dictionary<string, string>());
        TargetLecture = new Lecture(id, SelectedCourse.Id, language, fm, string.Empty);
        SelectedLecture = new LectureEntry(id, titleEn, nameRu, id);
        _answers = new();
        _answersDirty = false;
        _dirtyLectures.Add(id);
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    /// <summary>Renames a Тема (topic).</summary>
    public void RenameTopic(string topicId, string newTitle)
    {
        if (SelectedCourse is null) return;
        var topics = SelectedCourse.Topics
            .Select(t => t.Id == topicId ? t with { TitleEn = newTitle } : t)
            .ToList();
        SelectedCourse = SelectedCourse with { Topics = topics };
        IsMetadataDirty = true;
    }

    /// <summary>
    /// Deletes a Тема and all its Подтемы (their files too). Clears the current selection when the
    /// open subtopic belonged to the deleted topic.
    /// </summary>
    public void DeleteTopic(string topicId, string language)
    {
        if (SelectedCourse is null) return;

        var isLeaf = SelectedCourse.Topics.Any(t => t.IsLeaf && t.Id == topicId);
        var doomed = SelectedCourse.Lectures.Where(l => l.Topic == topicId).ToList();
        foreach (var lec in doomed)
            _repository.DeleteLecture(SelectedCourse.Id, lec.Id, language);
        // A leaf Тема stores its own content under the topic id — remove that file too.
        if (isLeaf) _repository.DeleteLecture(SelectedCourse.Id, topicId, language);

        var lectures = SelectedCourse.Lectures.Where(l => l.Topic != topicId).ToList();
        var topics = SelectedCourse.Topics.Where(t => t.Id != topicId).ToList();
        SelectedCourse = SelectedCourse with { Lectures = lectures, Topics = topics };
        IsMetadataDirty = true;

        if (SelectedTopicId == topicId) SelectedTopicId = null;
        var openLeaf = isLeaf && (TargetLecture?.Id == topicId || SelectedLecture?.Id == topicId);
        if (openLeaf || (SelectedLecture is { } sel && doomed.Any(l => l.Id == sel.Id)))
        {
            SelectedLecture = null;
            TargetLecture = null;
            _answers = new();
            _dirtyLectures.Remove(topicId);
            OnPropertyChanged(nameof(DirtyLectures));
            OnPropertyChanged(nameof(Answers));
        }
    }

    public void DeleteLecture(string lectureId, string language)
    {
        if (SelectedCourse is null) return;

        _repository.DeleteLecture(SelectedCourse.Id, lectureId, language);

        var lectures = SelectedCourse.Lectures.Where(l => l.Id != lectureId).ToList();
        // If this id is a leaf Тема's own content, drop the (now content-less) Тема as well.
        var topics = SelectedCourse.Topics.Where(t => !(t.IsLeaf && t.Id == lectureId)).ToList();
        SelectedCourse = SelectedCourse with { Lectures = lectures, Topics = topics };
        IsMetadataDirty = true;

        if (SelectedTopicId == lectureId) SelectedTopicId = null;
        if (SelectedLecture?.Id == lectureId)
        {
            SelectedLecture = null;
            TargetLecture = null;
            _answers = new();
            OnPropertyChanged(nameof(Answers));
        }
    }

    /// <summary>
    /// Deletes a course and clears the selection when it was the active one. The repository reloads
    /// the manifest (firing <c>ManifestChanged</c>), which the top-bar selector uses to fall back to
    /// another course.
    /// </summary>
    public void DeleteCourse(string courseId)
    {
        _repository.DeleteCourse(courseId);

        if (SelectedCourse?.Id == courseId)
        {
            SelectedCourse = null;
            SelectedLecture = null;
            TargetLecture = null;
            _answers = new();
            _answersDirty = false;
            _dirtyLectures.Clear();
            IsMetadataDirty = false;
            OnPropertyChanged(nameof(DirtyLectures));
            OnPropertyChanged(nameof(Answers));
        }
    }

    /// <summary>
    /// Clears every selection and draft when the backing course pack is replaced, so the constructor
    /// stops showing content from the old pack. The cached <see cref="SelectedCourse"/> was read from
    /// the previous source; dropping it lets the top-bar selector re-pick from the new manifest (see
    /// <c>CourseConstructorControlPanel.EnsureSelection</c>). Any unsaved edits to the old pack are
    /// discarded — switching packs is an explicit, destructive action.
    /// </summary>
    public void ResetSelection()
    {
        SelectedCourse = null;
        SelectedLecture = null;
        SelectedTopicId = null;
        TargetLecture = null;
        _answers = new();
        _answersDirty = false;
        _dirtyLectures.Clear();
        IsMetadataDirty = false;
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    public void CreateCourse(string id, string titleEn, string? nameRu)
    {
        SelectedCourse = new Course(id, titleEn, nameRu, null, new[] { "en" }, System.Array.Empty<LectureEntry>(), System.Array.Empty<string>(), System.Array.Empty<TopicEntry>());
        SelectedLecture = null;
        SelectedTopicId = null;
        TargetLecture = null;
        _answers = new();
        _answersDirty = false;
        IsMetadataDirty = true;
        _dirtyLectures.Clear();
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    public async Task SaveAsync()
    {
        var course = SelectedCourse;
        if (course is null) return;

        IsSaving = true;
        try
        {
            var lecture = TargetLecture;
            var answersJson = JsonSerializer.Serialize(_answers);
            var answersDirty = _answersDirty;
            var lectureDirty = lecture is not null && _dirtyLectures.Contains(lecture.Id);
            var metaDirty = IsMetadataDirty;

            await Task.Run(() =>
            {
                // Write the lecture content (and answers) first, then the course.txt/manifest last —
                // WriteCourse raises ManifestChanged, so keep it after the content is safely on disk.
                if (lecture is not null && lectureDirty) _repository.WriteLecture(lecture);
                if (lecture is not null && (answersDirty || lectureDirty))
                    _repository.WriteAnswers(lecture.CourseId, lecture.Id, lecture.Language, answersJson);
                if (metaDirty) _repository.WriteCourse(course);
            });

            _dirtyLectures.Clear();
            _answersDirty = false;
            IsMetadataDirty = false;
            OnPropertyChanged(nameof(DirtyLectures));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
