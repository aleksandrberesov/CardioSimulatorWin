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
        // SourceChanged first: it signals a *different pack*, so listeners can drop selection state that
        // belonged to the old source before ManifestChanged repopulates the course list.
        SourceChanged?.Invoke(this, EventArgs.Empty);
        ManifestChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The active source (e.g. for export re-packaging via <see cref="IContentPackExportable"/>).</summary>
    public ICourseSource Source => _source;

    /// <summary>Raised only when the backing source is replaced (a different content pack is loaded),
    /// unlike <see cref="ManifestChanged"/> which also fires on an in-place reload after a save. Lets
    /// listeners invalidate selection that pointed into the previous pack. Fires just before the
    /// accompanying <see cref="ManifestChanged"/>.</summary>
    public event EventHandler? SourceChanged;

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
        (_source as IWritableCourseSource)?.ReadAnswers(courseId, lectureId, language);

    /// <summary>
    /// Resolves a <c>coursehost</c> request path ("&lt;courseId&gt;/rel/path.ext") to raw bytes from
    /// the active source (encrypted pack or files). Used by the lecture WebView to serve course
    /// assets in memory. Returns null for a malformed path or a missing asset.
    /// </summary>
    public byte[]? ReadCourseAsset(string hostPath)
    {
        var trimmed = (hostPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash <= 0) return null;
        var courseId = trimmed[..slash];
        var relative = trimmed[(slash + 1)..];
        if (relative.Length == 0) return null;
        return _source.ReadAsset(courseId, relative);
    }

    public bool WriteLecture(Lecture lecture) =>
        WithFileSource(s => s.WriteLecture(lecture));

    public bool WriteLectureRaw(string courseId, string lectureId, string language, string body) =>
        WithFileSource(s => s.WriteLectureRaw(courseId, lectureId, language, body));

    public bool WriteAnswers(string courseId, string lectureId, string language, string json) =>
        WithFileSource(s => s.WriteAnswers(courseId, lectureId, language, json));

    public bool DeleteLecture(string courseId, string lectureId, string language) =>
        WithFileSource(s => s.DeleteLecture(courseId, lectureId, language));

    /// <summary>Deletes a course (folder + manifest row) and reloads the manifest so listeners refresh.</summary>
    public bool DeleteCourse(string courseId)
    {
        return WithFileSource(s =>
        {
            var ok = s.DeleteCourse(courseId);
            LoadManifest();
            return ok;
        });
    }

    public bool WriteCourse(Course course)
    {
        return WithFileSource(s =>
        {
            var ok = s.WriteCourse(course);
            if (ok) LoadManifest();
            return ok;
        });
    }

    private bool WithFileSource(Func<IWritableCourseSource, bool> block)
    {
        if (_source is IWritableCourseSource fs) return block(fs);
        return false;
    }
}
