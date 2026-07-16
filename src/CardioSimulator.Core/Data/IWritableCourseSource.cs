using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// A course source that also supports the course-constructor's write operations. Implemented by
/// <see cref="FileCourseSource"/> (loose author files) and <see cref="OverlayCourseSource"/> (an
/// encrypted writable overlay over a read-only content pack). <see cref="CourseRepository"/> routes
/// writes through this interface.
/// </summary>
public interface IWritableCourseSource : ICourseSource
{
    bool WriteLecture(Lecture lecture);
    bool WriteCourse(Course course);
    bool WriteLectureRaw(string courseId, string lectureId, string language, string body);
    bool WriteAnswers(string courseId, string lectureId, string language, string json);
    string? ReadAnswers(string courseId, string lectureId, string language);
    bool DeleteLecture(string courseId, string lectureId, string language);
    bool DeleteCourse(string courseId);
}
