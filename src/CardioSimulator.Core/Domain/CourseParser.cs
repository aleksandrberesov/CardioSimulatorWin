using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
        foreach (var line in body)
        {
            var fields = ParserHelpers.ParseSemicolonFields(line);
            var lectureId = ParserHelpers.Get(fields, "lecture");
            if (lectureId is null) continue;
            
            lectures.Add(new LectureEntry(
                Id: lectureId,
                TitleEn: ParserHelpers.Get(fields, "title") ?? string.Empty,
                NameRu: ParserHelpers.Get(fields, "name")
            ));
        }

        return new Course(
            Id: id,
            TitleEn: ParserHelpers.Get(header, "title") ?? string.Empty,
            NameRu: ParserHelpers.Get(header, "name"),
            Authors: ParserHelpers.Get(header, "authors"),
            Languages: languages,
            Lectures: lectures,
            Pathologies: pathologies
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
        
        foreach (var l in course.Lectures)
        {
            sb.Append("lecture:").Append(l.Id)
              .Append(";title:").Append(l.TitleEn);
              
            if (!string.IsNullOrWhiteSpace(l.NameRu)) sb.Append(";name:").Append(l.NameRu);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── <lecture-id>.<lang>.md ──────────────────────────────────────────────────

    public static Lecture ParseLecture(string text, string courseId, string language)
    {
        var (fmText, body) = SplitFrontMatter(text);
        var fm = ParseFrontMatter(fmText);
        var blocks = ExtractBlocks(body);
        
        return new Lecture(
            Id: fm.Id,
            CourseId: courseId,
            Language: language,
            FrontMatter: fm,
            Blocks: blocks,
            RawMarkdown: body
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
        sb.Append(lecture.RawMarkdown);
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

    // ─── fenced-block extraction ─────────────────────────────────────────────────

    private static readonly Regex FenceLine = new(@"^```\s*(\w+)\s*$");

    private static IReadOnlyList<CourseBlock> ExtractBlocks(string body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<CourseBlock>();
        
        var outBlocks = new List<CourseBlock>();
        var current = new StringBuilder();
        var lines = body.Split('\n').ToList();

        void FlushMarkdown()
        {
            var text = current.ToString();
            if (!string.IsNullOrEmpty(text)) outBlocks.Add(new CourseBlock.Markdown(text));
            current.Clear();
        }

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var match = FenceLine.Match(line.TrimEnd('\r'));
            var tag = match.Success ? match.Groups[1].Value : null;
            
            if (tag == "ecg" || tag == "table")
            {
                FlushMarkdown();
                var end = lines.Count;
                for (var idx = i + 1; idx < lines.Count; idx++)
                {
                    if (lines[idx].TrimEnd('\r').Trim() == "```")
                    {
                        end = idx;
                        break;
                    }
                }
                
                var innerCount = Math.Max(0, end - (i + 1));
                var inner = string.Join("\n", lines.GetRange(i + 1, innerCount));
                
                if (tag == "ecg") outBlocks.Add(ParseEcgFence(inner));
                else if (tag == "table") outBlocks.Add(ParseTableFence(inner));
                
                i = end + 1;
                continue;
            }
            
            current.Append(line);
            if (i != lines.Count - 1) current.Append('\n');
            i++;
        }
        FlushMarkdown();
        return outBlocks;
    }

    private static CourseBlock.EcgEmbed ParseEcgFence(string inner)
    {
        var fields = ParserHelpers.ParseKeyValueLines(inner);
        var pathology = ParserHelpers.Get(fields, "pathology") ?? "";
        var leadToken = ParserHelpers.Get(fields, "lead");
        Lead? lead = string.IsNullOrWhiteSpace(leadToken) ? null : Leads.FromToken(leadToken);
        var caption = ParserHelpers.Get(fields, "caption");
        if (string.IsNullOrWhiteSpace(caption)) caption = null;
        
        return new CourseBlock.EcgEmbed(pathology, lead, caption);
    }

    private static CourseBlock.EditableTable ParseTableFence(string inner)
    {
        var lines = inner.Split('\n').ToList();
        var separator = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---")
            {
                separator = i;
                break;
            }
        }
        
        var headerLines = separator >= 0 ? lines.GetRange(0, separator) : new List<string>();
        var rawLines = separator >= 0 ? lines.GetRange(separator + 1, lines.Count - separator - 1) : lines;
        
        var header = ParserHelpers.ParseKeyValueLines(string.Join("\n", headerLines));
        
        var id = ParserHelpers.Get(header, "id") ?? "";
        var editableStr = ParserHelpers.Get(header, "editable")?.Trim().ToLowerInvariant();
        var editable = editableStr == "true";
        var raw = string.Join("\n", rawLines).Trim('\n');
        
        return new CourseBlock.EditableTable(id, editable, raw);
    }

    private static IReadOnlyList<string> ParseCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
}
