using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>Top-level manifest.txt model.</summary>
public record CourseManifest(
    string Version,
    IReadOnlyList<CourseEntry> Entries)
{
    public const string SupportedVersion = "1.0";
}

/// <summary>One row of CourseManifest.Entries.</summary>
public record CourseEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    int LecturesCount,
    IReadOnlyList<string> Pathologies);

/// <summary>Parsed &lt;course-id&gt;/course.txt.</summary>
public record Course(
    string Id,
    string TitleEn,
    string? NameRu,
    string? Authors,
    IReadOnlyList<string> Languages,
    IReadOnlyList<LectureEntry> Lectures,
    IReadOnlyList<string> Pathologies);

/// <summary>One row of Course.Lectures.</summary>
public record LectureEntry(
    string Id,
    string TitleEn,
    string? NameRu);

/// <summary>
/// Parsed &lt;lecture-id&gt;.&lt;lang&gt;.html. The body after the front matter is kept
/// verbatim on <see cref="RawHtml"/> (rendered in a WebView2; not decomposed into blocks).
/// </summary>
public record Lecture(
    string Id,
    string CourseId,
    string Language,
    LectureFrontMatter FrontMatter,
    string RawHtml)
{
    /// <summary>
    /// True when <see cref="RawHtml"/> is a complete standalone HTML document pasted verbatim via
    /// the "All in one" mode. The renderer serves it as-is (only layering KaTeX / &lt;ecg&gt; / the
    /// quiz bridge on top) instead of wrapping a body fragment. Persisted as the front-matter extra
    /// <c>layout: standalone</c>.
    /// </summary>
    public bool IsStandalone =>
        FrontMatter.Extras.TryGetValue("layout", out var v) &&
        string.Equals(v, "standalone", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Front-matter key: value pairs.</summary>
public record LectureFrontMatter(
    string Id,
    int Order,
    string Title,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> Extras);
