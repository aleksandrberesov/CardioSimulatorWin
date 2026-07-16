using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// An inert <see cref="IPathologySource"/> that reports no data. Used as a repository's initial
/// source before a content pack is loaded, so the app never starts out pointed at a directory it is
/// no longer allowed to read (packs are the only accepted dataset format). Every read returns
/// null/empty rather than throwing, so a UI that queries before load simply shows nothing.
/// </summary>
public sealed class EmptyPathologySource : IPathologySource
{
    public PathologyManifest? ReadManifest() => null;
    public PathologyFile? ReadPathology(string id) => null;
    public IReadOnlyList<string> ListPathologies() => Array.Empty<string>();
}

/// <summary>The <see cref="ICourseSource"/> counterpart of <see cref="EmptyPathologySource"/>.</summary>
public sealed class EmptyCourseSource : ICourseSource
{
    public CourseManifest? ReadManifest() => null;
    public Course? ReadCourse(string courseId) => null;
    public Lecture? ReadLecture(string courseId, string lectureId, string language) => null;
    public IReadOnlyList<string> ListCourses() => Array.Empty<string>();
    public IReadOnlyList<string> ListLectures(string courseId) => Array.Empty<string>();
    public byte[]? ReadAsset(string courseId, string relativePath) => null;
}
