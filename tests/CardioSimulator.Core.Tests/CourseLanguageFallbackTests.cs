using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// A course pack can declare one <c>language</c> in course.txt while its lecture files carry a
/// different <c>&lt;id&gt;.&lt;lang&gt;.html</c> suffix (seen in the field: <c>language: en</c> but
/// <c>&lt;id&gt;.ru.html</c> files). Reads must still surface the content by falling back to the
/// language actually present, or every lecture reads empty — "loads empty, only the structure appears".
/// </summary>
public class CourseLanguageFallbackTests : IDisposable
{
    private readonly string _dir;
    private readonly string _basePak;

    public CourseLanguageFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs_lang_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _basePak = Path.Combine(_dir, "base.pak");
        WriteMismatchedPak(_basePak);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // course.txt says `language: en`; the only lecture file is `<id>.ru.html`.
    private static void WriteMismatchedPak(string path)
    {
        var manifest = new CourseManifest("1.0", new[]
        {
            new CourseEntry("ecg", "ECG", null, 1, Array.Empty<string>()),
        });
        var course = new Course("ecg", "ECG", null, null,
            new[] { "en" },                                   // declares en...
            new[] { new LectureEntry("intro", "Intro", null, null) },
            Array.Empty<string>(), Array.Empty<TopicEntry>());

        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(z, "manifest.txt", CourseParser.SerializeManifest(manifest));
                Add(z, "ecg/course.txt", CourseParser.SerializeCourse(course));
                Add(z, "ecg/lectures/intro.ru.html", "---\nid: intro\n---\n<p>RU CONTENT</p>"); // ...but ships ru
            }
            zip = ms.ToArray();
        }
        File.WriteAllBytes(path, ContentCrypto.Encrypt(zip));
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    [Fact]
    public void EncryptedSource_reads_lecture_despite_declared_language_mismatch()
    {
        using var src = EncryptedCourseSource.Open(_basePak);

        var lecture = src.ReadLecture("ecg", "intro", "en"); // ask for the declared language
        Assert.NotNull(lecture);
        Assert.Equal("ru", lecture!.Language);               // resolved to the language on disk
        Assert.Contains("RU CONTENT", lecture.RawHtml);
    }

    [Fact]
    public void OverlaySource_reads_lecture_despite_declared_language_mismatch()
    {
        var ov = new OverlayCourseSource(
            EncryptedCourseSource.Open(_basePak),
            WritableEncryptedOverlay.OpenOrCreate(Path.Combine(_dir, "overlay.pak")));

        var lecture = ov.ReadLecture("ecg", "intro", "en");
        Assert.NotNull(lecture);
        Assert.Contains("RU CONTENT", lecture!.RawHtml);
        Assert.Contains("intro", ov.ListLectures("ecg"));    // ListLectures probes via ReadLecture("en")
    }

    [Fact]
    public void Requested_language_still_wins_when_present()
    {
        // Adding an en file must not be shadowed by the fallback: an explicit en request gets en.
        var pak = Path.Combine(_dir, "both.pak");
        var manifest = new CourseManifest("1.0", new[] { new CourseEntry("ecg", "ECG", null, 1, Array.Empty<string>()) });
        var course = new Course("ecg", "ECG", null, null, new[] { "ru", "en" },
            new[] { new LectureEntry("intro", "Intro", null, null) }, Array.Empty<string>(), Array.Empty<TopicEntry>());
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(z, "manifest.txt", CourseParser.SerializeManifest(manifest));
                Add(z, "ecg/course.txt", CourseParser.SerializeCourse(course));
                Add(z, "ecg/lectures/intro.ru.html", "---\nid: intro\n---\n<p>RU CONTENT</p>");
                Add(z, "ecg/lectures/intro.en.html", "---\nid: intro\n---\n<p>EN CONTENT</p>");
            }
            zip = ms.ToArray();
        }
        File.WriteAllBytes(pak, ContentCrypto.Encrypt(zip));

        using var src = EncryptedCourseSource.Open(pak);
        Assert.Contains("EN CONTENT", src.ReadLecture("ecg", "intro", "en")!.RawHtml);
        Assert.Contains("RU CONTENT", src.ReadLecture("ecg", "intro", "ru")!.RawHtml);
    }
}
