using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

public partial class CourseViewerViewModel : ObservableObject
{
    private readonly CourseRepository _repository;

    [ObservableProperty]
    private Course? _selectedCourse;

    [ObservableProperty]
    private LectureEntry? _selectedLecture;

    [ObservableProperty]
    private Lecture? _lectureContent;

    public CourseViewerViewModel(CourseRepository repository)
    {
        _repository = repository;
    }

    public void SelectCourse(string id)
    {
        SelectedCourse = _repository.ReadCourse(id);
        SelectedLecture = null;
        LectureContent = null;
    }

    public void SelectLecture(string lectureId, string language)
    {
        if (SelectedCourse is null) return;
        SelectedLecture = SelectedCourse.Lectures.FirstOrDefault(l => l.Id == lectureId);
        LectureContent = _repository.ReadLecture(SelectedCourse.Id, lectureId, language);
    }
}
