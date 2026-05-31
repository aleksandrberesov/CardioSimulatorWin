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

/// <summary>Parsed &lt;lecture-id&gt;.&lt;lang&gt;.md.</summary>
public record Lecture(
    string Id,
    string CourseId,
    string Language,
    LectureFrontMatter FrontMatter,
    IReadOnlyList<CourseBlock> Blocks,
    string RawMarkdown);

/// <summary>Front-matter key: value pairs.</summary>
public record LectureFrontMatter(
    string Id,
    int Order,
    string Title,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> Extras);

/// <summary>One renderable segment of a lecture body.</summary>
public abstract record CourseBlock
{
    private CourseBlock() { }

    public record Markdown(string Text) : CourseBlock;

    public record EcgEmbed(
        string PathologyId,
        Lead? Lead,
        string? Caption) : CourseBlock;

    public record EditableTable(
        string Id,
        bool Editable,
        string Raw) : CourseBlock;
}
