using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Exercises <see cref="OverlayCourseSource"/> over a real encrypted course pack: read pass-through,
/// lecture override, tombstone-aware language fallback, course deletion, and merged export.
/// </summary>
public class ContentCourseOverlayTests : IDisposable
{
    private readonly string _dir;
    private readonly string _basePak;
    private readonly string _overlayPak;

    public ContentCourseOverlayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs_course_overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _basePak = Path.Combine(_dir, "base.pak");
        _overlayPak = Path.Combine(_dir, "overlay.pak");
        WriteBasePak(_basePak);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private OverlayCourseSource NewOverlay() =>
        new(EncryptedCourseSource.Open(_basePak), WritableEncryptedOverlay.OpenOrCreate(_overlayPak));

    [Fact]
    public void Reads_pass_through_to_base_when_overlay_empty()
    {
        var ov = NewOverlay();
        Assert.Equal(new[] { "cardio" }, ov.ListCourses());
        Assert.Equal("cardio", ov.ReadCourse("cardio")!.Id);
        Assert.Contains("RU", ov.ReadLecture("cardio", "x", "ru")!.RawHtml);
        Assert.Contains("EN", ov.ReadLecture("cardio", "x", "en")!.RawHtml);
    }

    [Fact]
    public void Edit_lecture_overrides_without_touching_the_pack()
    {
        var baseBytes = File.ReadAllBytes(_basePak);
        var ov = NewOverlay();

        Assert.True(ov.WriteLectureRaw("cardio", "x", "ru", "<p>EDITED</p>"));
        Assert.Contains("EDITED", ov.ReadLecture("cardio", "x", "ru")!.RawHtml);
        Assert.Contains("EDITED", NewOverlay().ReadLecture("cardio", "x", "ru")!.RawHtml);
        Assert.Equal(baseBytes, File.ReadAllBytes(_basePak));
    }

    [Fact]
    public void Deleting_one_language_falls_back_to_the_surviving_one()
    {
        var ov = NewOverlay();
        Assert.True(ov.DeleteLecture("cardio", "x", "ru"));

        // ru is tombstoned, but a ru-locale reader must still get the en fallback, not null.
        var lecture = ov.ReadLecture("cardio", "x", "ru");
        Assert.NotNull(lecture);
        Assert.Contains("EN", lecture!.RawHtml);
        Assert.Contains("x", ov.ListLectures("cardio")); // still listed (en survives)
    }

    [Fact]
    public void Delete_course_tombstones_it_but_keeps_the_pack()
    {
        var baseBytes = File.ReadAllBytes(_basePak);
        var ov = NewOverlay();

        Assert.True(ov.DeleteCourse("cardio"));
        Assert.Null(ov.ReadCourse("cardio"));
        Assert.DoesNotContain("cardio", ov.ListCourses());
        Assert.DoesNotContain("cardio", NewOverlay().ListCourses()); // persisted
        Assert.Equal(baseBytes, File.ReadAllBytes(_basePak));
    }

    [Fact]
    public void Export_excludes_a_tombstoned_course()
    {
        var ov = NewOverlay();
        ov.DeleteCourse("cardio");

        var packBytes = ContentPackWriter.BuildEncryptedPack(ov);
        using var reopened = EncryptedArchive.OpenBytes(packBytes);
        var names = reopened.EntryPaths.ToList();

        Assert.Contains("manifest.txt", names);
        Assert.DoesNotContain("cardio/course.txt", names);
        Assert.DoesNotContain("cardio/lectures/x.ru.html", names);
    }

    [Fact]
    public void Export_preserves_entry_content_not_just_structure()
    {
        // Guards against a pack that re-exports every entry NAME but with empty/wrong bytes — the
        // "loads empty, only the structure appears" failure. Reads the content back through the full
        // streaming export -> reopen path (the one the app's Export button uses), including an edit.
        var ov = NewOverlay();
        Assert.True(ov.WriteLectureRaw("cardio", "x", "ru", "---\nid: x\n---\n<p>EDITED RU</p>"));

        var dir = Path.Combine(Path.GetTempPath(), "cs_export_content_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPak = Path.Combine(dir, "export.pak");
            ContentPackWriter.WriteEncryptedPack(ov, outPak); // streaming (non-seekable) writer

            using var reopened = EncryptedCourseSource.Open(outPak);
            Assert.NotNull(reopened.ReadManifest());
            Assert.NotNull(reopened.ReadCourse("cardio"));
            Assert.Contains("EDITED RU", reopened.ReadLecture("cardio", "x", "ru")!.RawHtml); // overlay edit
            Assert.Contains("EN", reopened.ReadLecture("cardio", "x", "en")!.RawHtml);         // base pass-through
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void WriteBasePak(string path)
    {
        var manifest = new CourseManifest("1.0", new[]
        {
            new CourseEntry("cardio", "Cardio", null, 1, Array.Empty<string>()),
        });
        var course = new Course(
            "cardio", "Cardio", null, null,
            new[] { "ru", "en" },
            new[] { new LectureEntry("x", "Lesson", null, null) },
            Array.Empty<string>(), Array.Empty<TopicEntry>());

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(zip, "manifest.txt", CourseParser.SerializeManifest(manifest));
                AddEntry(zip, "cardio/course.txt", CourseParser.SerializeCourse(course));
                AddEntry(zip, "cardio/lectures/x.ru.html", "---\nid: x\n---\n<p>RU</p>");
                AddEntry(zip, "cardio/lectures/x.en.html", "---\nid: x\n---\n<p>EN</p>");
            }
            zipBytes = ms.ToArray();
        }
        File.WriteAllBytes(path, ContentCrypto.Encrypt(zipBytes));
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
