using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

public class CourseRepository
{
    private ICourseSource _source;
    private CourseManifest? _manifest;

    public CourseRepository(ICourseSource source)
    {
        _source = source;
    }

    public void SetSource(ICourseSource newSource)
    {
        _source = newSource;
        _manifest = null;
        ManifestChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ManifestChanged;

    public bool LoadManifest()
    {
        var m = _source.ReadManifest();
        _manifest = m;
        ManifestChanged?.Invoke(this, EventArgs.Empty);
        return m != null;
    }

    public CourseManifest? Manifest => _manifest;

    public IReadOnlyList<CourseEntry> Courses =>
        _manifest?.Entries.OrderBy(x => x.TitleEn.ToLowerInvariant()).ToList() 
        ?? (IReadOnlyList<CourseEntry>)Array.Empty<CourseEntry>();

    public Course? ReadCourse(string courseId) => _source.ReadCourse(courseId);

    public Lecture? ReadLecture(string courseId, string lectureId, string language) =>
        _source.ReadLecture(courseId, lectureId, language);

    public IReadOnlyList<LectureEntry> LectureEntries(string courseId) =>
        ReadCourse(courseId)?.Lectures ?? (IReadOnlyList<LectureEntry>)Array.Empty<LectureEntry>();

    public string? ReadAnswers(string courseId, string lectureId, string language) =>
        (_source as FileCourseSource)?.ReadAnswers(courseId, lectureId, language);

    public bool WriteLecture(Lecture lecture) =>
        WithFileSource(s => s.WriteLecture(lecture));

    public bool WriteLectureRaw(string courseId, string lectureId, string language, string body) =>
        WithFileSource(s => s.WriteLectureRaw(courseId, lectureId, language, body));

    public bool WriteAnswers(string courseId, string lectureId, string language, string json) =>
        WithFileSource(s => s.WriteAnswers(courseId, lectureId, language, json));

    public bool DeleteLecture(string courseId, string lectureId, string language) =>
        WithFileSource(s => s.DeleteLecture(courseId, lectureId, language));

    public bool WriteCourse(Course course)
    {
        return WithFileSource(s =>
        {
            var ok = s.WriteCourse(course);
            if (ok) LoadManifest();
            return ok;
        });
    }

    private bool WithFileSource(Func<FileCourseSource, bool> block)
    {
        if (_source is FileCourseSource fs) return block(fs);
        return false;
    }
}
