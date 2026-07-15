using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read-only <see cref="ICourseSource"/> backed by an encrypted content pack
/// (<see cref="EncryptedArchive"/>) held entirely in memory. This is the distribution read path for
/// course content: the bundle ships as <c>Courses.pak</c> and is never extracted to disk, so no
/// lecture <c>.html</c> or course asset ever lands in <c>%LOCALAPPDATA%</c>.
///
/// <para>Course packs preserve their directory tree (mirroring the on-disk layout the old extractor
/// produced): <c>manifest.txt</c> at the root, then
/// <c>&lt;courseId&gt;/course.txt</c> and <c>&lt;courseId&gt;/lectures/&lt;id&gt;.&lt;lang&gt;.html</c>.
/// Being read-only, it does not implement the constructor's write/delete methods that
/// <see cref="FileCourseSource"/> adds.</para>
/// </summary>
public sealed class EncryptedCourseSource : ICourseSource, IDisposable
{
    private const string FallbackLang = "en";

    private readonly EncryptedArchive _archive;

    public EncryptedCourseSource(EncryptedArchive archive)
    {
        _archive = archive;
    }

    /// <summary>Opens the pack at <paramref name="packPath"/> and wraps it. Throws on a bad pack.</summary>
    public static EncryptedCourseSource Open(string packPath) =>
        new(EncryptedArchive.Open(packPath));

    private static IEnumerable<string> FallbackLanguages(string language)
    {
        if (language != FallbackLang) yield return language;
        yield return FallbackLang;
    }

    public CourseManifest? ReadManifest()
    {
        try
        {
            var text = _archive.ReadPathText("manifest.txt");
            return text is null ? null : CourseParser.ParseManifest(text);
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
            var text = _archive.ReadPathText($"{courseId}/course.txt");
            return text is null ? null : CourseParser.ParseCourse(text);
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
            var text = _archive.ReadPathText($"{courseId}/lectures/{lectureId}.{lang}.html");
            if (text is null) continue;
            try
            {
                return CourseParser.ParseLecture(text, courseId, lang);
            }
            catch
            {
                // swallow and try the next fallback language
            }
        }
        return null;
    }

    public IReadOnlyList<string> ListCourses()
    {
        try
        {
            // Any entry "<courseId>/course.txt" marks a course; take the first path segment.
            return _archive.EntryPaths
                .Where(p => p.EndsWith("/course.txt", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[..p.IndexOf('/')])
                .Distinct(StringComparer.Ordinal)
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
            var prefix = $"{courseId}/lectures/";
            return _archive.EntryPaths
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            p.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[prefix.Length..])                        // "<id>.<lang>.html"
                .Select(Path.GetFileNameWithoutExtension)               // "<id>.<lang>"
                .Where(n => n is not null)
                .Select(n => Path.GetFileNameWithoutExtension(n!))      // "<id>" (strip language code)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public byte[]? ReadAsset(string courseId, string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        // Reject traversal out of the course subtree.
        if (rel.Contains("../", StringComparison.Ordinal) || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal))
            return null;
        return _archive.ReadPath($"{courseId}/{rel}");
    }

    /// <summary>Matches <see cref="FileCourseSource.IsValid"/>: a course manifest is present.</summary>
    public bool IsValid() => ReadManifest() is not null;

    public void Dispose() => _archive.Dispose();
}
