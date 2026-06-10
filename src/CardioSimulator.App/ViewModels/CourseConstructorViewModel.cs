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

    /// <summary>Saved quiz answers for the current lecture (quizId → "row,col" → value).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Answers =>
        _answers.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);

    public void SelectCourse(string id)
    {
        SelectedCourse = _repository.ReadCourse(id);
        SelectedLecture = null;
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
        SelectedLecture = SelectedCourse.Lectures.FirstOrDefault(l => l.Id == lectureId);

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
        TargetLecture = TargetLecture with { RawHtml = text };
        _dirtyLectures.Add(TargetLecture.Id);
        OnPropertyChanged(nameof(DirtyLectures));
    }

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

    public void RenameLecture(string newTitle)
    {
        if (SelectedCourse is null || TargetLecture is null) return;
        var fm = TargetLecture.FrontMatter with { Title = newTitle };
        TargetLecture = TargetLecture with { FrontMatter = fm };
        var lectures = SelectedCourse.Lectures
            .Select(l => l.Id == TargetLecture.Id ? l with { TitleEn = newTitle } : l)
            .ToList();
        SelectedCourse = SelectedCourse with { Lectures = lectures };
        SelectedLecture = lectures.FirstOrDefault(l => l.Id == TargetLecture.Id);
        _dirtyLectures.Add(TargetLecture.Id);
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

    public void CreateLecture(string id, string language, string titleEn, string? nameRu)
    {
        if (SelectedCourse is null) return;

        var fm = new LectureFrontMatter(id, 0, titleEn, 1, new Dictionary<string, string>());
        TargetLecture = new Lecture(id, SelectedCourse.Id, language, fm, string.Empty);

        var lectures = SelectedCourse.Lectures.ToList();
        lectures.Add(new LectureEntry(id, titleEn, nameRu));
        SelectedCourse = SelectedCourse with { Lectures = lectures };
        SelectedLecture = lectures[^1];

        _answers = new();
        _answersDirty = false;
        _dirtyLectures.Add(id);
        IsMetadataDirty = true;
        OnPropertyChanged(nameof(DirtyLectures));
        OnPropertyChanged(nameof(Answers));
    }

    public void DeleteLecture(string lectureId, string language)
    {
        if (SelectedCourse is null) return;

        _repository.DeleteLecture(SelectedCourse.Id, lectureId, language);

        var lectures = SelectedCourse.Lectures.Where(l => l.Id != lectureId).ToList();
        SelectedCourse = SelectedCourse with { Lectures = lectures };
        IsMetadataDirty = true;

        if (SelectedLecture?.Id == lectureId)
        {
            SelectedLecture = null;
            TargetLecture = null;
            _answers = new();
            OnPropertyChanged(nameof(Answers));
        }
    }

    public void CreateCourse(string id, string titleEn, string? nameRu)
    {
        SelectedCourse = new Course(id, titleEn, nameRu, null, new[] { "en" }, System.Array.Empty<LectureEntry>(), System.Array.Empty<string>());
        SelectedLecture = null;
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
                if (metaDirty) _repository.WriteCourse(course);
                if (lecture is not null && lectureDirty) _repository.WriteLecture(lecture);
                if (lecture is not null && (answersDirty || lectureDirty))
                    _repository.WriteAnswers(lecture.CourseId, lecture.Id, lecture.Language, answersJson);
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
