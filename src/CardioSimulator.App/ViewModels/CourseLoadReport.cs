using System.Collections.Generic;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Result of loading a course pack, surfaced to the user after an explicit "Change courses" import so
/// they can see <i>what actually loaded</i> — not just that the action returned. Carries a per-course
/// breakdown plus a short plain-text <see cref="PreviewSnippet"/> read from a real lecture, which is
/// the concrete evidence that lecture <i>content</i> (not merely the structure/manifest) came through.
/// </summary>
public sealed record CourseLoadReport(
    bool Success,
    string FileName,
    IReadOnlyList<CourseLoadSummary> Courses,
    int TotalLectures,
    string? PreviewCourseTitle,
    string? PreviewLectureTitle,
    string? PreviewSnippet)
{
    public int CourseCount => Courses.Count;

    /// <summary>True when the manifest advertises lectures but not one of them yielded readable body
    /// text — the "loads empty, only the structure appears" state, now shown to the user instead of
    /// failing silently.</summary>
    public bool StructureWithoutContent =>
        Success && TotalLectures > 0 && string.IsNullOrEmpty(PreviewSnippet);
}

/// <summary>One course's line in a <see cref="CourseLoadReport"/>.</summary>
public sealed record CourseLoadSummary(
    string Title,
    int LectureCount,
    IReadOnlyList<string> Languages);
