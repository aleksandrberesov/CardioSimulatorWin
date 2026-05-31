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
}
