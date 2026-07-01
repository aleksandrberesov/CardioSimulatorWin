using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CardioSimulator.Core.Domain;

public sealed class CourseFormatException : Exception
{
    public CourseFormatException(string message) : base(message) { }
}

public static class CourseParser
{
    // ─── manifest.txt ────────────────────────────────────────────────────────────

    public static CourseManifest ParseManifest(string text)
    {
        var (header, body) = ParserHelpers.SplitHeader(text);
        
        var version = ParserHelpers.Get(header, "version") 
            ?? throw new CourseFormatException("manifest: missing 'version'");
        
        if (version != CourseManifest.SupportedVersion)
        {
            throw new CourseFormatException(
                $"manifest: unsupported version '{version}' (this build needs '{CourseManifest.SupportedVersion}')");
        }

        var entries = new List<CourseEntry>();
        foreach (var line in body)
        {
            var fields = ParserHelpers.ParseSemicolonFields(line);
            var id = ParserHelpers.Get(fields, "course");
            if (id is null) continue;

            entries.Add(new CourseEntry(
                Id: id,
                TitleEn: ParserHelpers.Get(fields, "title") ?? string.Empty,
                NameRu: ParserHelpers.Get(fields, "name"),
                LecturesCount: ParserHelpers.ToIntOrNull(ParserHelpers.Get(fields, "lectures")) ?? 0,
                Pathologies: ParseCsv(ParserHelpers.Get(fields, "pathologies"))
            ));
        }

        return new CourseManifest(version, entries);
    }

    public static string SerializeManifest(CourseManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("version:").Append(manifest.Version).Append('\n');
        sb.Append("courses:").Append(manifest.Entries.Count).Append('\n');
        sb.Append('\n');
        
        foreach (var e in manifest.Entries)
        {
            sb.Append("course:").Append(e.Id)
              .Append(";lectures:").Append(e.LecturesCount)
              .Append(";title:").Append(e.TitleEn);
            
            if (!string.IsNullOrWhiteSpace(e.NameRu)) sb.Append(";name:").Append(e.NameRu);
            if (e.Pathologies.Count > 0)
            {
                sb.Append(";pathologies:").Append(string.Join(",", e.Pathologies));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── <course-id>/course.txt ──────────────────────────────────────────────────

    public static Course ParseCourse(string text)
    {
        var (header, body) = ParserHelpers.SplitHeader(text);
        var id = ParserHelpers.Get(header, "course") 
            ?? throw new CourseFormatException("course: missing 'course'");
            
        var languagesRaw = ParserHelpers.Get(header, "language");
        var languages = string.IsNullOrWhiteSpace(languagesRaw) 
            ? (IReadOnlyList<string>)Array.Empty<string>() 
            : languagesRaw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            
        var pathologies = ParseCsv(ParserHelpers.Get(header, "pathologies"));

        var lectures = new List<LectureEntry>();
        var topics = new List<TopicEntry>();
        foreach (var line in body)
        {
            var fields = ParserHelpers.ParseSemicolonFields(line);

            // A "topic:" line declares a Тема (grouping) with its display names + implicit order.
            var topicId = ParserHelpers.Get(fields, "topic");
            var lectureId = ParserHelpers.Get(fields, "lecture");
            if (lectureId is null && topicId is not null)
            {
                topics.Add(new TopicEntry(
                    Id: topicId,
                    TitleEn: ParserHelpers.Get(fields, "title") ?? string.Empty,
                    NameRu: ParserHelpers.Get(fields, "name")
                ));
                continue;
            }

            if (lectureId is null) continue;

            lectures.Add(new LectureEntry(
                Id: lectureId,
                TitleEn: ParserHelpers.Get(fields, "title") ?? string.Empty,
                NameRu: ParserHelpers.Get(fields, "name"),
                // A "lecture:" line may carry ";topic:<id>" to place it under a Тема.
                Topic: topicId
            ));
        }

        return new Course(
            Id: id,
            TitleEn: ParserHelpers.Get(header, "title") ?? string.Empty,
            NameRu: ParserHelpers.Get(header, "name"),
            Authors: ParserHelpers.Get(header, "authors"),
            Languages: languages,
            Lectures: lectures,
            Pathologies: pathologies,
            Topics: topics
        );
    }

    public static string SerializeCourse(Course course)
    {
        var sb = new StringBuilder();
        sb.Append("course:").Append(course.Id).Append('\n');
        sb.Append("title:").Append(course.TitleEn).Append('\n');
        
        if (!string.IsNullOrWhiteSpace(course.NameRu)) sb.Append("name:").Append(course.NameRu).Append('\n');
        if (!string.IsNullOrWhiteSpace(course.Authors)) sb.Append("authors:").Append(course.Authors).Append('\n');
        
        if (course.Languages.Count > 0)
        {
            sb.Append("language:").Append(string.Join(",", course.Languages)).Append('\n');
        }
        if (course.Pathologies.Count > 0)
        {
            sb.Append("pathologies:").Append(string.Join(",", course.Pathologies)).Append('\n');
        }
        sb.Append('\n');

        // Тема definitions first (order = their order here), then the lectures that reference them.
        foreach (var t in course.Topics)
        {
            sb.Append("topic:").Append(t.Id)
              .Append(";title:").Append(t.TitleEn);
            if (!string.IsNullOrWhiteSpace(t.NameRu)) sb.Append(";name:").Append(t.NameRu);
            sb.Append('\n');
        }

        foreach (var l in course.Lectures)
        {
            sb.Append("lecture:").Append(l.Id)
              .Append(";title:").Append(l.TitleEn);

            if (!string.IsNullOrWhiteSpace(l.NameRu)) sb.Append(";name:").Append(l.NameRu);
            if (!string.IsNullOrWhiteSpace(l.Topic)) sb.Append(";topic:").Append(l.Topic);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── <lecture-id>.<lang>.html ────────────────────────────────────────────────

    /// <summary>
    /// Parses a lecture file. <paramref name="courseId"/> / <paramref name="language"/> are
    /// injected by the source layer (encoded in the path, not the content). The HTML body after
    /// the front matter is kept verbatim on <see cref="Lecture.RawHtml"/>. Port of Android
    /// <c>CourseParser.parseLecture</c>.
    /// </summary>
    public static Lecture ParseLecture(string text, string courseId, string language)
    {
        var (fmText, body) = SplitFrontMatter(text);
        var fm = ParseFrontMatter(fmText);

        return new Lecture(
            Id: fm.Id,
            CourseId: courseId,
            Language: language,
            FrontMatter: fm,
            RawHtml: body
        );
    }

    public static string SerializeLecture(Lecture lecture)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        var fm = lecture.FrontMatter;
        sb.Append("id: ").Append(fm.Id).Append('\n');
        if (fm.Order != 0) sb.Append("order: ").Append(fm.Order).Append('\n');
        if (!string.IsNullOrEmpty(fm.Title)) sb.Append("title: ").Append(fm.Title).Append('\n');
        sb.Append("schemaVersion: ").Append(fm.SchemaVersion).Append('\n');

        foreach (var kvp in fm.Extras)
        {
            sb.Append(kvp.Key).Append(": ").Append(kvp.Value).Append('\n');
        }
        sb.Append("---\n");
        sb.Append(lecture.RawHtml);
        return sb.ToString();
    }

    // ─── front matter ────────────────────────────────────────────────────────────

    private static (string Header, string Body) SplitFrontMatter(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.FirstOrDefault()?.Trim() != "---") return ("", text);
        
        var closeIdx = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeIdx = i;
                break;
            }
        }
        if (closeIdx == -1) return ("", text);
        
        var header = string.Join("\n", lines.GetRange(1, closeIdx - 1));
        var body = string.Join("\n", lines.GetRange(closeIdx + 1, lines.Count - closeIdx - 1)).TrimStart('\n');
        return (header, body);
    }

    private static LectureFrontMatter ParseFrontMatter(string text)
    {
        var id = "";
        var order = 0;
        var title = "";
        var schemaVersion = 1;
        var extras = new Dictionary<string, string>();
        
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var kv = ParserHelpers.SplitKeyValue(line);
            if (kv is null) continue;
            
            var k = kv.Value.Key.Trim();
            var value = kv.Value.Value.Trim();
            
            switch (k)
            {
                case "id": id = value; break;
                case "order": order = ParserHelpers.ToIntOrNull(value) ?? 0; break;
                case "title": title = value; break;
                case "schemaVersion": schemaVersion = ParserHelpers.ToIntOrNull(value) ?? 1; break;
                default: extras[k] = value; break;
            }
        }
        
        return new LectureFrontMatter(id, order, title, schemaVersion, extras);
    }

    private static IReadOnlyList<string> ParseCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
}
