using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

public partial class CourseConstructorViewModel : ObservableObject
{
    private readonly CourseRepository _repository;
    private readonly HashSet<string> _dirtyLectures = new();

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

    /// <summary>Exposes the backing repository so the constructor screen can list courses + read lectures.</summary>
    public CourseRepository Repository => _repository;

    public IReadOnlyCollection<string> DirtyLectures => _dirtyLectures.ToArray();

    public void SelectCourse(string id)
    {
        SelectedCourse = _repository.ReadCourse(id);
        SelectedLecture = null;
        TargetLecture = null;
        _dirtyLectures.Clear();
        OnPropertyChanged(nameof(DirtyLectures));
        IsMetadataDirty = false;
    }

    public void SelectLecture(string lectureId, string language)
    {
        if (SelectedCourse is null) return;
        SelectedLecture = SelectedCourse.Lectures.FirstOrDefault(l => l.Id == lectureId);
        TargetLecture = _repository.ReadLecture(SelectedCourse.Id, lectureId, language);
    }

    public void SetMarkdown(string text)
    {
        if (TargetLecture is null) return;
        TargetLecture = TargetLecture with { RawMarkdown = text };
        _dirtyLectures.Add(TargetLecture.Id);
        OnPropertyChanged(nameof(DirtyLectures));
    }

    public void SetFrontMatter(LectureFrontMatter frontMatter)
    {
        if (TargetLecture is null) return;
        TargetLecture = TargetLecture with { FrontMatter = frontMatter };
        _dirtyLectures.Add(TargetLecture.Id);
        OnPropertyChanged(nameof(DirtyLectures));
    }

    public void SetTableCell(string tableId, int row, int col, string value)
    {
        // For Phase 2, this is a placeholder. 
        // We persist answers separately to `.answers.json`.
    }

    public void RevertLecture()
    {
        if (SelectedCourse is null || TargetLecture is null) return;
        TargetLecture = _repository.ReadLecture(SelectedCourse.Id, TargetLecture.Id, TargetLecture.Language);
        _dirtyLectures.Remove(TargetLecture?.Id ?? "");
        OnPropertyChanged(nameof(DirtyLectures));
    }

    public void CreateLecture(string id, string language, string titleEn, string? nameRu)
    {
        if (SelectedCourse is null) return;
        
        var fm = new LectureFrontMatter(id, 0, titleEn, 1, new Dictionary<string, string>());
        var newLecture = new Lecture(id, SelectedCourse.Id, language, fm, Array.Empty<CourseBlock>(), "");
        TargetLecture = newLecture;
        
        var lectures = SelectedCourse.Lectures.ToList();
        lectures.Add(new LectureEntry(id, titleEn, nameRu));
        SelectedCourse = SelectedCourse with { Lectures = lectures };
        
        _dirtyLectures.Add(id);
        IsMetadataDirty = true;
        OnPropertyChanged(nameof(DirtyLectures));
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
        }
    }

    public void CreateCourse(string id, string titleEn, string? nameRu)
    {
        SelectedCourse = new Course(id, titleEn, nameRu, null, new[] { "en" }, Array.Empty<LectureEntry>(), Array.Empty<string>());
        SelectedLecture = null;
        TargetLecture = null;
        IsMetadataDirty = true;
        _dirtyLectures.Clear();
        OnPropertyChanged(nameof(DirtyLectures));
    }

    public async Task SaveAsync()
    {
        var course = SelectedCourse;
        if (course is null) return;
        
        IsSaving = true;
        try
        {
            await Task.Run(() =>
            {
                if (IsMetadataDirty)
                {
                    _repository.WriteCourse(course);
                }
                
                if (TargetLecture is not null && _dirtyLectures.Contains(TargetLecture.Id))
                {
                    _repository.WriteLecture(TargetLecture);
                }
            });
            
            _dirtyLectures.Clear();
            IsMetadataDirty = false;
            OnPropertyChanged(nameof(DirtyLectures));
        }
        finally
        {
            IsSaving = false;
        }
    }
}
