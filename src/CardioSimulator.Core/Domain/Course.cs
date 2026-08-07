using System;
using System.Collections.Generic;
using System.Linq;

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
    IReadOnlyList<string> Pathologies,
    IReadOnlyList<TopicEntry> Topics)
{
    /// <summary>
    /// Resolves a clickable content item by id to a <see cref="LectureEntry"/>: either a real Подтема
    /// (<c>Course → Тема → Подтема</c>) or a <b>leaf Тема</b> (<c>Course → Тема</c>), which carries its
    /// own lecture and is surfaced as a <see cref="LectureEntry"/> whose <see cref="LectureEntry.Topic"/>
    /// points at itself. Returns null when nothing matches. Both kinds map to
    /// <c>lectures/&lt;id&gt;.&lt;lang&gt;.html</c>, so the caller loads either the same way.
    /// </summary>
    public LectureEntry? ContentItem(string id) =>
        Lectures.FirstOrDefault(l => l.Id == id)
        ?? Topics.Where(t => t.IsLeaf && t.Id == id)
                 .Select(t => new LectureEntry(t.Id, t.TitleEn, t.NameRu, t.Id))
                 .FirstOrDefault();

    /// <summary>
    /// The id of the item that should open first, mirroring the navigation order the flyout builds:
    /// ungrouped lectures, then each Тема in order (a leaf Тема itself, or a group's first Подтема).
    /// Null when the course has no content at all.
    /// </summary>
    public string? FirstContentItemId()
    {
        var known = Topics.Select(t => t.Id).ToHashSet();
        var ungrouped = Lectures.FirstOrDefault(l => string.IsNullOrEmpty(l.Topic) || !known.Contains(l.Topic!));
        if (ungrouped is not null) return ungrouped.Id;

        foreach (var topic in Topics)
        {
            if (topic.IsLeaf) return topic.Id;
            var first = Lectures.FirstOrDefault(l => l.Topic == topic.Id);
            if (first is not null) return first.Id;
        }
        return Lectures.Count > 0 ? Lectures[0].Id : null;
    }
}

/// <summary>
/// A "Тема" (topic) within a course. Two shapes, chosen per-topic by the author:
/// <list type="bullet">
/// <item><b>Group</b> (<see cref="IsLeaf"/> = false): a named grouping whose <see cref="Id"/> is
/// referenced by each member Подтема's <see cref="LectureEntry.Topic"/> — the classic
/// <c>Course → Тема → Подтема</c> nesting. It has no content of its own and may be empty.</item>
/// <item><b>Leaf</b> (<see cref="IsLeaf"/> = true): a content-bearing Тема with no Подтемы — the
/// two-level <c>Course → Тема</c> shape. Its lecture is stored like any other, keyed by the topic
/// id (<c>lectures/&lt;Id&gt;.&lt;lang&gt;.html</c>), and it is surfaced through
/// <see cref="Course.ContentItem"/>.</item>
/// </list>
/// Topics carry ordering (their order in <see cref="Course.Topics"/>) and localized names.
/// </summary>
public record TopicEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    bool IsLeaf = false);

/// <summary>
/// One row of Course.Lectures — a leaf "Подтема" (subtopic) whose HTML content is stored on disk.
/// <see cref="Topic"/> is the id of the <see cref="TopicEntry"/> it belongs to, or null when the
/// lecture is ungrouped (e.g. legacy courses authored before topics existed).
/// </summary>
public record LectureEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    string? Topic = null);

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

    /// <summary>
    /// Returns a copy whose front-matter <c>layout: standalone</c> flag is consistent with the current
    /// content: set only while <see cref="RawHtml"/> is a complete document, and cleared once it is a body
    /// fragment — so <see cref="IsStandalone"/> never goes stale (e.g. after a pasted page is decomposed into
    /// an embedded-page fragment to combine it with app components). Rendering is content-driven regardless;
    /// this just keeps the persisted metadata truthful. Returns <c>this</c> when already consistent.
    /// </summary>
    public Lecture WithReconciledLayout()
    {
        var shouldBeStandalone = HtmlCompiler.IsFullDocument(RawHtml);
        if (shouldBeStandalone == IsStandalone) return this;

        var extras = new Dictionary<string, string>(FrontMatter.Extras);
        if (shouldBeStandalone) extras["layout"] = "standalone";
        else extras.Remove("layout");
        return this with { FrontMatter = FrontMatter with { Extras = extras } };
    }
}

/// <summary>Front-matter key: value pairs.</summary>
public record LectureFrontMatter(
    string Id,
    int Order,
    string Title,
    int SchemaVersion,
    IReadOnlyDictionary<string, string> Extras);
