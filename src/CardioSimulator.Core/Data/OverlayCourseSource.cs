using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// A writable course source that layers an encrypted <see cref="WritableEncryptedOverlay"/> over a
/// read-only base course pack, so the Full-edition course constructor can create/edit/delete
/// courses and lectures on a protected build without writing lecture HTML or assets as plaintext.
///
/// <para>Copy-on-write: reads resolve overlay-first then base; writes go only to the overlay
/// (keyed by the same archive-relative paths the base uses — <c>&lt;courseId&gt;/course.txt</c>,
/// <c>&lt;courseId&gt;/lectures/&lt;id&gt;.&lt;lang&gt;.html</c>, <c>.answers.json</c>, assets).
/// Deleting a bundled course or lecture records a tombstone; the pack stays intact.</para>
/// </summary>
public sealed class OverlayCourseSource : IWritableCourseSource, IContentPackExportable
{
    private const string ManifestName = "manifest.txt";
    private const string TombstonesName = "deleted.txt";
    private const string FallbackLang = "en";

    private readonly ICourseSource _base;
    private readonly WritableEncryptedOverlay _overlay;

    public OverlayCourseSource(ICourseSource baseSource, WritableEncryptedOverlay overlay)
    {
        _base = baseSource;
        _overlay = overlay;
    }

    private static IEnumerable<string> FallbackLanguages(string language)
    {
        if (language != FallbackLang) yield return language;
        yield return FallbackLang;
    }

    // ── tombstones ("course:<id>" / "lecture:<courseId>/<id>.<lang>") ───────

    private (HashSet<string> courses, HashSet<string> lectures) LoadTombstones()
    {
        var courses = new HashSet<string>(StringComparer.Ordinal);
        var lectures = new HashSet<string>(StringComparer.Ordinal);
        if (_overlay.ReadText(TombstonesName) is { } text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("course:", StringComparison.Ordinal)) courses.Add(line["course:".Length..]);
                else if (line.StartsWith("lecture:", StringComparison.Ordinal)) lectures.Add(line["lecture:".Length..]);
            }
        }
        return (courses, lectures);
    }

    private void SaveTombstones(HashSet<string> courses, HashSet<string> lectures)
    {
        var lines = courses.Select(c => "course:" + c).Concat(lectures.Select(l => "lecture:" + l));
        _overlay.PutText(TombstonesName, string.Join('\n', lines));
    }

    private static string LectureKey(string courseId, string lectureId, string language) =>
        $"{courseId}/{lectureId}.{language}";

    // ── overlay manifest (delta CourseEntries) ──────────────────────────────

    private List<CourseEntry> LoadOverlayEntries()
    {
        if (_overlay.ReadText(ManifestName) is { } text)
        {
            try { return CourseParser.ParseManifest(text).Entries.ToList(); }
            catch { /* fall through */ }
        }
        return new List<CourseEntry>();
    }

    private void SaveOverlayEntries(IReadOnlyList<CourseEntry> entries)
    {
        var version = _base.ReadManifest()?.Version ?? CourseManifest.SupportedVersion;
        _overlay.PutText(ManifestName, CourseParser.SerializeManifest(new CourseManifest(version, entries)));
    }

    private void UpsertOverlayEntry(CourseEntry entry)
    {
        var entries = LoadOverlayEntries();
        var idx = entries.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0) entries[idx] = entry; else entries.Add(entry);
        SaveOverlayEntries(entries);
    }

    // ── reads (overlay over base) ───────────────────────────────────────────

    public CourseManifest? ReadManifest()
    {
        var baseM = _base.ReadManifest();
        var overlayEntries = LoadOverlayEntries();
        var (courseTomb, _) = LoadTombstones();
        var overlayById = overlayEntries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var version = baseM?.Version ?? CourseManifest.SupportedVersion;
        var merged = new List<CourseEntry>();
        var baseIds = new HashSet<string>(StringComparer.Ordinal);
        if (baseM is not null)
        {
            foreach (var e in baseM.Entries)
            {
                baseIds.Add(e.Id);
                if (courseTomb.Contains(e.Id)) continue;
                merged.Add(overlayById.TryGetValue(e.Id, out var oe) ? oe : e);
            }
        }
        foreach (var oe in overlayEntries)
        {
            if (!baseIds.Contains(oe.Id) && !courseTomb.Contains(oe.Id)) merged.Add(oe);
        }
        if (baseM is null && merged.Count == 0) return null;
        return new CourseManifest(version, merged);
    }

    public Course? ReadCourse(string courseId)
    {
        var (courseTomb, _) = LoadTombstones();
        if (courseTomb.Contains(courseId)) return null;
        if (_overlay.ReadText($"{courseId}/course.txt") is { } text)
        {
            try { return CourseParser.ParseCourse(text); }
            catch { return null; }
        }
        return _base.ReadCourse(courseId);
    }

    public Lecture? ReadLecture(string courseId, string lectureId, string language)
    {
        var (courseTomb, lectureTomb) = LoadTombstones();
        if (courseTomb.Contains(courseId)) return null;

        // Walk the fallback languages once, resolving each against overlay-then-base and skipping any
        // tombstoned language — so deleting only the primary language still falls back to a surviving
        // one instead of hiding the lecture entirely.
        foreach (var lang in FallbackLanguages(language))
        {
            if (lectureTomb.Contains(LectureKey(courseId, lectureId, lang))) continue;

            if (_overlay.ReadText($"{courseId}/lectures/{lectureId}.{lang}.html") is { } text)
            {
                try { return CourseParser.ParseLecture(text, courseId, lang); }
                catch { /* try next language */ }
            }

            var baseLecture = _base.ReadLecture(courseId, lectureId, lang);
            if (baseLecture is not null && !lectureTomb.Contains(LectureKey(courseId, lectureId, baseLecture.Language)))
                return baseLecture;
        }
        return null;
    }

    public IReadOnlyList<string> ListCourses()
    {
        var (courseTomb, _) = LoadTombstones();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _base.ListCourses()) if (!courseTomb.Contains(id)) ids.Add(id);
        foreach (var path in _overlay.EntryPaths)
        {
            const string suffix = "/course.txt";
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var id = path[..^suffix.Length];
                if (!courseTomb.Contains(id)) ids.Add(id);
            }
        }
        return ids.ToList();
    }

    public IReadOnlyList<string> ListLectures(string courseId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _base.ListLectures(courseId)) ids.Add(id);
        var prefix = $"{courseId}/lectures/";
        foreach (var path in _overlay.EntryPaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                var file = path[prefix.Length..];                               // "<id>.<lang>.html"
                var noHtml = Path.GetFileNameWithoutExtension(file);            // "<id>.<lang>"
                ids.Add(Path.GetFileNameWithoutExtension(noHtml));              // "<id>"
            }
        }
        // Drop ids whose every language variant is tombstoned (consistent with ReadLecture, which
        // returns null for them). ReadLecture resolves overlay-then-base with language fallback.
        return ids.Where(id => ReadLecture(courseId, id, FallbackLang) is not null).ToList();
    }

    public byte[]? ReadAsset(string courseId, string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0 || rel.Contains("../", StringComparison.Ordinal)) return _base.ReadAsset(courseId, relativePath);
        return _overlay.Read($"{courseId}/{rel}") ?? _base.ReadAsset(courseId, relativePath);
    }

    // ── writes (overlay only) ───────────────────────────────────────────────

    public bool WriteLecture(Lecture lecture) =>
        WriteLectureRaw(lecture.CourseId, lecture.Id, lecture.Language, CourseParser.SerializeLecture(lecture));

    public bool WriteLectureRaw(string courseId, string lectureId, string language, string body)
    {
        try
        {
            _overlay.PutText($"{courseId}/lectures/{lectureId}.{language}.html", body);
            ClearLectureTombstone(courseId, lectureId, language);
            return true;
        }
        catch { return false; }
    }

    public bool WriteCourse(Course course)
    {
        try
        {
            _overlay.PutText($"{course.Id}/course.txt", CourseParser.SerializeCourse(course));
            UpsertOverlayEntry(new CourseEntry(course.Id, course.TitleEn, course.NameRu, course.Lectures.Count, course.Pathologies));
            var (courseTomb, lectureTomb) = LoadTombstones();
            if (courseTomb.Remove(course.Id)) SaveTombstones(courseTomb, lectureTomb);
            return true;
        }
        catch { return false; }
    }

    public bool WriteAnswers(string courseId, string lectureId, string language, string json)
    {
        try
        {
            _overlay.PutText($"{courseId}/lectures/{lectureId}.{language}.answers.json", json);
            return true;
        }
        catch { return false; }
    }

    public string? ReadAnswers(string courseId, string lectureId, string language) =>
        _overlay.ReadText($"{courseId}/lectures/{lectureId}.{language}.answers.json");

    public bool DeleteLecture(string courseId, string lectureId, string language)
    {
        try
        {
            _overlay.Delete($"{courseId}/lectures/{lectureId}.{language}.html");
            _overlay.Delete($"{courseId}/lectures/{lectureId}.{language}.answers.json");
            if (_base.ReadLecture(courseId, lectureId, language) is not null)
            {
                var (courseTomb, lectureTomb) = LoadTombstones();
                if (lectureTomb.Add(LectureKey(courseId, lectureId, language))) SaveTombstones(courseTomb, lectureTomb);
            }
            return true;
        }
        catch { return false; }
    }

    public bool DeleteCourse(string courseId)
    {
        try
        {
            var prefix = $"{courseId}/";
            foreach (var path in _overlay.EntryPaths)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal)) _overlay.Delete(path);
            }
            var entries = LoadOverlayEntries();
            if (entries.RemoveAll(e => e.Id == courseId) > 0) SaveOverlayEntries(entries);

            if (_base.ListCourses().Contains(courseId))
            {
                var (courseTomb, lectureTomb) = LoadTombstones();
                if (courseTomb.Add(courseId)) SaveTombstones(courseTomb, lectureTomb);
            }
            return true;
        }
        catch { return false; }
    }

    private void ClearLectureTombstone(string courseId, string lectureId, string language)
    {
        var (courseTomb, lectureTomb) = LoadTombstones();
        if (lectureTomb.Remove(LectureKey(courseId, lectureId, language))) SaveTombstones(courseTomb, lectureTomb);
    }

    // ── export (merged, for a distributable pak) ────────────────────────────

    public IEnumerable<KeyValuePair<string, byte[]>> ExportEntries()
    {
        var (courseTomb, lectureTomb) = LoadTombstones();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        // Overlay content wins; skip the overlay's own bookkeeping entries.
        foreach (var path in _overlay.EntryPaths)
        {
            if (path == ManifestName || path == TombstonesName) continue;
            if (IsPathTombstoned(path, courseTomb, lectureTomb)) continue;
            if (_overlay.Read(path) is { } bytes)
            {
                emitted.Add(path);
                yield return new KeyValuePair<string, byte[]>(path, bytes);
            }
        }

        // Base entries not overridden and not tombstoned (base manifest is replaced by the merge).
        if (_base is IContentPackExportable exportableBase)
        {
            foreach (var kv in exportableBase.ExportEntries())
            {
                if (kv.Key.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                if (emitted.Contains(kv.Key)) continue;
                if (IsPathTombstoned(kv.Key, courseTomb, lectureTomb)) continue;
                yield return kv;
            }
        }

        // Merged manifest.
        if (ReadManifest() is { } manifest)
            yield return new KeyValuePair<string, byte[]>(
                ManifestName, System.Text.Encoding.UTF8.GetBytes(CourseParser.SerializeManifest(manifest)));
    }

    private static bool IsPathTombstoned(string path, HashSet<string> courseTomb, HashSet<string> lectureTomb)
    {
        foreach (var courseId in courseTomb)
            if (path.StartsWith(courseId + "/", StringComparison.Ordinal)) return true;

        foreach (var key in lectureTomb) // key = "<courseId>/<id>.<lang>"
        {
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var courseId = key[..slash];
            var idLang = key[(slash + 1)..];
            if (path == $"{courseId}/lectures/{idLang}.html" ||
                path == $"{courseId}/lectures/{idLang}.answers.json") return true;
        }
        return false;
    }
}
