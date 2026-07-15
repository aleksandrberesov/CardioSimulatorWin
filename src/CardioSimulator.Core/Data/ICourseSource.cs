using System.Collections.Generic;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

public interface ICourseSource
{
    CourseManifest? ReadManifest();
    Course? ReadCourse(string courseId);
    Lecture? ReadLecture(string courseId, string lectureId, string language);
    IReadOnlyList<string> ListCourses();
    IReadOnlyList<string> ListLectures(string courseId);

    /// <summary>
    /// Raw bytes of a course asset (image, font, …) at <paramref name="relativePath"/> under
    /// <paramref name="courseId"/>, or null if absent. Used to serve <c>coursehost</c> resources to
    /// the lecture WebView without exposing loose files on disk. Implementations must reject paths
    /// that escape the course directory.
    /// </summary>
    byte[]? ReadAsset(string courseId, string relativePath);
}
