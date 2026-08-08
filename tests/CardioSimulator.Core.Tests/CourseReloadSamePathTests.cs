using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// A user who re-exports a course pack over the <i>same path</i> and reloads it must see the new
/// content. The lazy on-disk reader used for the large pathology dataset holds the file open for the
/// source's lifetime (<c>FileShare.Read</c> denies writers), which would block the re-export and make
/// the reload re-read the old bytes — "the dataset doesn't change". Course packs are therefore read
/// fully into memory so the file is never held open. These tests pin that behaviour.
/// </summary>
public class CourseReloadSamePathTests : IDisposable
{
    private readonly string _dir;

    public CourseReloadSamePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs_reload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // A minimal one-lecture course pack whose lecture body is <paramref name="body"/>.
    private static void WritePak(string path, string body)
    {
        var manifest = new CourseManifest("1.0", new[]
        {
            new CourseEntry("ecg", "ECG", null, 1, Array.Empty<string>()),
        });
        var course = new Course("ecg", "ECG", null, null, new[] { "en" },
            new[] { new LectureEntry("intro", "Intro", null, null) },
            Array.Empty<string>(), Array.Empty<TopicEntry>());

        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(z, "manifest.txt", CourseParser.SerializeManifest(manifest));
                Add(z, "ecg/course.txt", CourseParser.SerializeCourse(course));
                Add(z, "ecg/lectures/intro.en.html", $"---\nid: intro\n---\n<p>{body}</p>");
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
    public void Open_does_not_hold_the_pack_file_open()
    {
        var pak = Path.Combine(_dir, "course.pak");
        WritePak(pak, "V1");

        using var src = EncryptedCourseSource.Open(pak);
        Assert.Contains("V1", src.ReadLecture("ecg", "intro", "en")!.RawHtml);

        // The file must be replaceable while the source is still open — a held handle would throw here.
        var ex = Record.Exception(() => WritePak(pak, "V2"));
        Assert.Null(ex);
    }

    [Fact]
    public void Reloading_the_same_path_reflects_new_content()
    {
        var pak = Path.Combine(_dir, "course.pak");

        WritePak(pak, "V1");
        using (var first = EncryptedCourseSource.Open(pak))
            Assert.Contains("V1", first.ReadLecture("ecg", "intro", "en")!.RawHtml);

        // Re-export new content over the same path, then reopen — the reload must show V2, not V1.
        WritePak(pak, "V2");
        using var second = EncryptedCourseSource.Open(pak);
        Assert.Contains("V2", second.ReadLecture("ecg", "intro", "en")!.RawHtml);
    }

    [Fact]
    public void Overlay_reloading_same_path_when_file_updated_shows_new_content()
    {
        var pak = Path.Combine(_dir, "course.pak");
        var overlay = Path.Combine(_dir, "overlay.pak");

        WritePak(pak, "V1");
        using (var firstBase = EncryptedCourseSource.Open(pak))
        {
            var firstOverlay = new OverlayCourseSource(firstBase, WritableEncryptedOverlay.OpenOrCreate(overlay));
            Assert.Contains("V1", firstOverlay.ReadLecture("ecg", "intro", "en")!.RawHtml);
        }

        // Wait slightly so file modification time changes
        System.Threading.Thread.Sleep(50);

        // Update the pack file at the same path
        WritePak(pak, "V2");
        using var secondBase = EncryptedCourseSource.Open(pak);
        var secondOverlay = new OverlayCourseSource(secondBase, WritableEncryptedOverlay.OpenOrCreate(overlay));
        Assert.Contains("V2", secondOverlay.ReadLecture("ecg", "intro", "en")!.RawHtml);
    }
}
