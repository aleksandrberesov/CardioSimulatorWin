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
public sealed class EncryptedCourseSource : ICourseSource, IContentPackExportable, IDisposable
{
    private const string FallbackLang = "en";

    private readonly EncryptedArchive _archive;

    public EncryptedCourseSource(EncryptedArchive archive)
    {
        _archive = archive;
    }

    /// <summary>
    /// Opens the pack at <paramref name="packPath"/> and wraps it. Throws on a bad pack.
    ///
    /// <para>The pack is read fully into memory and the file handle is released immediately, rather than
    /// held open for the session like the lazy on-disk reader (<see cref="EncryptedArchive.Open"/>). A
    /// held handle denies writers (<c>FileShare.Read</c>), so re-exporting a pack over a path already in
    /// use would fail and a reload would just re-read the old bytes — the dataset would appear not to
    /// change. Course packs are small, so paying their size in memory to keep the file replaceable is
    /// the right trade; the large pathology dataset keeps the lazy on-disk reader.</para>
    /// </summary>
    public static EncryptedCourseSource Open(string packPath) =>
        new(EncryptedArchive.OpenBytes(File.ReadAllBytes(packPath)));

    /// <summary>
    /// Languages to try for a lecture, in preference order: the requested one, then
    /// <see cref="FallbackLang"/>, then whatever language suffixes actually exist on disk for this
    /// lecture. The last step is what saves a course whose declared <c>language</c> does not match its
    /// files — e.g. a course marked <c>language: en</c> whose lectures were authored as
    /// <c>&lt;id&gt;.ru.html</c>. Without it the requested/en probes both miss and the lecture reads as
    /// empty ("loads empty, only the structure appears") even though its body is right there under
    /// another suffix. Only reached when the exact probes miss, so a well-formed pack pays nothing.
    /// </summary>
    private IEnumerable<string> FallbackLanguages(string courseId, string lectureId, string language)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(language)) yield return language;
        if (seen.Add(FallbackLang)) yield return FallbackLang;
        foreach (var lang in AvailableLanguages(courseId, lectureId))
            if (seen.Add(lang)) yield return lang;
    }

    /// <summary>Language suffixes present for a lecture, read from its
    /// <c>&lt;courseId&gt;/lectures/&lt;lectureId&gt;.&lt;lang&gt;.html</c> entries.</summary>
    private IEnumerable<string> AvailableLanguages(string courseId, string lectureId)
    {
        const string ext = ".html";
        var prefix = $"{courseId}/lectures/{lectureId}.";
        foreach (var path in _archive.EntryPaths)
        {
            if (path.Length <= prefix.Length + ext.Length) continue;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
            var lang = path[prefix.Length..^ext.Length];   // "<lang>"
            // Skip ".answers.json" siblings (already excluded by ext) and any dotted remainder.
            if (lang.Length > 0 && !lang.Contains('.')) yield return lang;
        }
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
        foreach (var lang in FallbackLanguages(courseId, lectureId, language))
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

    /// <summary>Verbatim pass-through of the pack's entries (re-exports an identical pack).</summary>
    public IEnumerable<KeyValuePair<string, byte[]>> ExportEntries()
    {
        foreach (var path in _archive.EntryPaths)
        {
            if (_archive.ReadPath(path) is { } bytes)
                yield return new KeyValuePair<string, byte[]>(path, bytes);
        }
    }

    /// <summary>Matches <see cref="FileCourseSource.IsValid"/>: a course manifest is present.</summary>
    public bool IsValid() => ReadManifest() is not null;

    public void Dispose() => _archive.Dispose();
}
