using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

public class FileCourseSource : ICourseSource
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const string FallbackLang = "en";

    public string Root { get; }

    public FileCourseSource(string root)
    {
        Root = root;
    }

    private static IEnumerable<string> FallbackLanguages(string language)
    {
        if (language != FallbackLang) yield return language;
        yield return FallbackLang;
    }

    public CourseManifest? ReadManifest()
    {
        try
        {
            var path = Path.Combine(Root, "manifest.txt");
            if (!File.Exists(path)) return null;
            return CourseParser.ParseManifest(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public Course? ReadCourse(string courseId)
    {
        try
        {
            var path = Path.Combine(Root, courseId, "course.txt");
            if (!File.Exists(path)) return null;
            return CourseParser.ParseCourse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public Lecture? ReadLecture(string courseId, string lectureId, string language)
    {
        foreach (var lang in FallbackLanguages(language))
        {
            var path = Path.Combine(Root, courseId, "lectures", $"{lectureId}.{lang}.html");
            if (!File.Exists(path)) continue;
            try
            {
                return CourseParser.ParseLecture(File.ReadAllText(path, Encoding.UTF8), courseId, lang);
            }
            catch
            {
                // swallow and try fallback
            }
        }
        return null;
    }

    public IReadOnlyList<string> ListCourses()
    {
        try
        {
            if (!Directory.Exists(Root)) return Array.Empty<string>();
            return Directory.GetDirectories(Root)
                .Where(dir => File.Exists(Path.Combine(dir, "course.txt")))
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> ListLectures(string courseId)
    {
        try
        {
            var dir = Path.Combine(Root, courseId, "lectures");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.html")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .Select(n => Path.GetFileNameWithoutExtension(n)) // strip language code
                .Distinct()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private bool AtomicWriteText(string target, string text)
    {
        try
        {
            var tmp = target + ".tmp";
            var dir = Path.GetDirectoryName(target);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteLecture(Lecture lecture) =>
        AtomicWriteText(
            Path.Combine(Root, lecture.CourseId, "lectures", $"{lecture.Id}.{lecture.Language}.html"),
            CourseParser.SerializeLecture(lecture));

    public bool WriteCourse(Course course)
    {
        var ok = AtomicWriteText(
            Path.Combine(Root, course.Id, "course.txt"),
            CourseParser.SerializeCourse(course));
        if (!ok) return false;
        return SyncManifestEntry(course);
    }

    public bool WriteLectureRaw(string courseId, string lectureId, string language, string body) =>
        AtomicWriteText(Path.Combine(Root, courseId, "lectures", $"{lectureId}.{language}.html"), body);

    public bool WriteAnswers(string courseId, string lectureId, string language, string json) =>
        AtomicWriteText(Path.Combine(Root, courseId, "lectures", $"{lectureId}.{language}.answers.json"), json);

    public string? ReadAnswers(string courseId, string lectureId, string language)
    {
        try
        {
            var path = Path.Combine(Root, courseId, "lectures", $"{lectureId}.{language}.answers.json");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Deletes an entire course: its folder (course.txt + all lectures/answers) and its manifest row.
    /// The manifest entry is dropped even if the folder removal partially fails, so a stale row never
    /// lingers in the rhythm/course lists.
    /// </summary>
    public bool DeleteCourse(string courseId)
    {
        var ok = true;
        var dir = Path.Combine(Root, courseId);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { ok = false; }
        }
        RemoveManifestEntry(courseId);
        return ok;
    }

    public bool DeleteLecture(string courseId, string lectureId, string language)
    {
        var html = Path.Combine(Root, courseId, "lectures", $"{lectureId}.{language}.html");
        var answers = Path.Combine(Root, courseId, "lectures", $"{lectureId}.{language}.answers.json");
        var ok = true;
        if (File.Exists(html))
        {
            try { File.Delete(html); } catch { ok = false; }
        }
        if (File.Exists(answers))
        {
            try { File.Delete(answers); } catch { ok = false; }
        }
        return ok;
    }

    public bool IsValid() => Directory.Exists(Root) && File.Exists(Path.Combine(Root, "manifest.txt"));

    private bool SyncManifestEntry(Course course)
    {
        var manifest = ReadManifest();
        if (manifest is null) return true; // no manifest yet, nothing to sync
        
        var entry = new CourseEntry(
            Id: course.Id,
            TitleEn: course.TitleEn,
            NameRu: course.NameRu,
            LecturesCount: course.Lectures.Count,
            Pathologies: course.Pathologies
        );
        
        var list = manifest.Entries.ToList();
        var idx = list.FindIndex(e => e.Id == course.Id);
        
        if (idx < 0)
        {
            list.Add(entry);
        }
        else if (list[idx] == entry)
        {
            return true; // no-op
        }
        else
        {
            list[idx] = entry;
        }
        
        var newManifest = new CourseManifest(manifest.Version, list);
        return AtomicWriteText(
            Path.Combine(Root, "manifest.txt"),
            CourseParser.SerializeManifest(newManifest));
    }

    private bool RemoveManifestEntry(string courseId)
    {
        var manifest = ReadManifest();
        if (manifest is null) return true; // no manifest yet, nothing to remove

        var list = manifest.Entries.Where(e => e.Id != courseId).ToList();
        if (list.Count == manifest.Entries.Count) return true; // wasn't listed, no-op

        var newManifest = new CourseManifest(manifest.Version, list);
        return AtomicWriteText(
            Path.Combine(Root, "manifest.txt"),
            CourseParser.SerializeManifest(newManifest));
    }
}
