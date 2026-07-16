using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Exercises <see cref="OverlayPathologySource"/> (encrypted copy-on-write overlay) over a real
/// <see cref="EncryptedPathologySource"/> base pack: reads merge, writes never touch the pack, the
/// overlay is encrypted at rest, and export re-packs the merged view.
/// </summary>
public class ContentOverlayTests : IDisposable
{
    private readonly string _dir;
    private readonly string _basePak;
    private readonly string _overlayPak;

    public ContentOverlayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs_overlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _basePak = Path.Combine(_dir, "base.pak");
        _overlayPak = Path.Combine(_dir, "overlay.pak");
        WriteBasePak(_basePak, "sinus", "afib");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private OverlayPathologySource NewOverlay() =>
        new(EncryptedPathologySource.Open(_basePak), WritableEncryptedOverlay.OpenOrCreate(_overlayPak));

    [Fact]
    public void Reads_pass_through_to_base_when_overlay_empty()
    {
        var ov = NewOverlay();
        Assert.Equal(new[] { "afib", "sinus" }, ov.ListPathologies().OrderBy(x => x));
        Assert.Equal(12, ov.ReadPathology("sinus")!.Leads.Count);
        Assert.Equal(2, ov.ReadManifest()!.Entries.Count);
    }

    [Fact]
    public void Create_adds_only_to_overlay_and_leaves_base_untouched()
    {
        var baseBytes = File.ReadAllBytes(_basePak);
        var ov = NewOverlay();

        var id = ov.CreatePathology("New Rhythm", null, 500, 1024);
        Assert.NotNull(id);
        Assert.Contains(id!, ov.ListPathologies());
        Assert.Contains(ov.ReadManifest()!.Entries, e => e.Id == id);

        // Base pack is never mutated; a fresh overlay instance still sees the created item.
        Assert.Equal(baseBytes, File.ReadAllBytes(_basePak));
        Assert.NotNull(NewOverlay().ReadPathology(id!));
    }

    [Fact]
    public void Edit_of_a_bundled_pathology_overrides_without_touching_the_pack()
    {
        var baseBytes = File.ReadAllBytes(_basePak);
        var ov = NewOverlay();

        var original = ov.ReadPathology("sinus")!;
        Assert.True(ov.WritePathology(original with { TitleEn = "Edited Sinus" }));

        Assert.Equal("Edited Sinus", ov.ReadManifest()!.Entries.Single(e => e.Id == "sinus").TitleEn);
        Assert.Equal("Edited Sinus", NewOverlay().ReadManifest()!.Entries.Single(e => e.Id == "sinus").TitleEn);
        Assert.Equal(baseBytes, File.ReadAllBytes(_basePak)); // pack byte-for-byte intact
    }

    [Fact]
    public void Delete_of_a_bundled_pathology_tombstones_it_but_keeps_the_pack()
    {
        var baseBytes = File.ReadAllBytes(_basePak);
        var ov = NewOverlay();

        Assert.True(ov.DeletePathology("afib"));
        Assert.DoesNotContain("afib", ov.ListPathologies());
        Assert.Null(ov.ReadPathology("afib"));
        Assert.DoesNotContain(ov.ReadManifest()!.Entries, e => e.Id == "afib");

        // Persisted across reopen; pack untouched.
        Assert.DoesNotContain("afib", NewOverlay().ListPathologies());
        Assert.Equal(baseBytes, File.ReadAllBytes(_basePak));
    }

    [Fact]
    public void Duplicate_copies_a_bundled_pathology_under_a_fresh_id()
    {
        var ov = NewOverlay();
        var newId = ov.DuplicatePathology("sinus");
        Assert.NotNull(newId);
        Assert.NotEqual("sinus", newId);
        Assert.Equal(12, ov.ReadPathology(newId!)!.Leads.Count);
    }

    [Fact]
    public void Overlay_is_encrypted_at_rest()
    {
        var ov = NewOverlay();
        ov.WritePathology(ov.ReadPathology("sinus")! with { TitleEn = "Secret Copy" });

        var raw = File.ReadAllBytes(_overlayPak);
        Assert.True(ContentCrypto.LooksLikePack(raw));                 // real pack header
        // The plaintext .dat marker / edited title must not appear in the on-disk bytes.
        Assert.DoesNotContain("Secret Copy", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("leadorder", Encoding.ASCII.GetString(raw).ToLowerInvariant());
    }

    [Fact]
    public void Export_repacks_the_merged_view_excluding_tombstones()
    {
        var ov = NewOverlay();
        var created = ov.CreatePathology("Made Up", null, 500, 1024)!;
        ov.DeletePathology("afib");

        var packBytes = ContentPackWriter.BuildEncryptedPack(ov);
        using var reopened = EncryptedArchive.OpenBytes(packBytes);
        var names = reopened.EntryPaths.ToList();

        Assert.Contains("manifest.txt", names);
        Assert.Contains("sinus.dat", names);
        Assert.Contains($"{created}.dat", names);
        Assert.DoesNotContain("afib.dat", names); // tombstoned item excluded from the exported pack
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void WriteBasePak(string path, params string[] ids)
    {
        var entries = ids
            .Select(id => new PathologyEntry(id, id + " title", null, Leads.All.Count, id + ".dat"))
            .ToList();
        var manifest = new PathologyManifest("1.0", 1024, Leads.All, entries);

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(zip, "manifest.txt", PathologyParser.SerializeManifest(manifest));
                foreach (var id in ids)
                {
                    var leads = Leads.All.ToDictionary(l => l, l => new LeadStream(l, Filled(1024, 500)));
                    var file = new PathologyFile(id, id + " title", null, leads);
                    AddEntry(zip, id + ".dat", PathologyParser.SerializePathology(file, Leads.All));
                }
                AddEntry(zip, "groups.txt", "version:1.0\ngroups:1\n\ngroup:sinus;en:Sinus\n");
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

    private static int[] Filled(int value, int count)
    {
        var a = new int[count];
        Array.Fill(a, value);
        return a;
    }
}
