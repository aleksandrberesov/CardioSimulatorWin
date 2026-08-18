using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Regression guard for the ECG-export failure: <see cref="OverlayPathologySource.ExportEntries"/> used
/// to decode every pathology and re-encode it to CSD1 16-bit delta binary, so a single record whose
/// amplitude exceeded the 16-bit range — a text-format record such as a WFDB import — threw
/// <see cref="PathologyFormatException"/> and aborted the whole export ("Export failed"). Export now
/// copies <c>.dat</c> bytes through verbatim, so those records survive losslessly and never break it.
/// </summary>
public class OverlayExportPassThroughTests : IDisposable
{
    private const int OutOfRangeSample = 40000; // > short.MaxValue (32767): unencodable as a 16-bit delta

    private readonly string _dir;
    private readonly string _basePak;
    private readonly string _overlayPak;

    public OverlayExportPassThroughTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs_export_passthrough_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _basePak = Path.Combine(_dir, "base.pak");
        _overlayPak = Path.Combine(_dir, "overlay.pak");
        WriteBasePakWithWideRangeRecord(_basePak);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private OverlayPathologySource NewOverlay() =>
        new(EncryptedPathologySource.Open(_basePak), WritableEncryptedOverlay.OpenOrCreate(_overlayPak));

    [Fact]
    public void Streaming_export_does_not_throw_on_a_sample_beyond_16_bit()
    {
        var outPak = Path.Combine(_dir, "export.pak");

        var ex = Record.Exception(() => ContentPackWriter.WriteEncryptedPack(NewOverlay(), outPak));
        Assert.Null(ex); // used to throw PathologyFormatException from the re-encode

        using var reopened = EncryptedPathologySource.Open(outPak);
        var wide = reopened.ReadPathology("wide");
        Assert.NotNull(wide);
        // Verbatim pass-through preserves the out-of-range amplitude; a re-encode would clamp or throw.
        Assert.Contains(wide!.Leads.Values.SelectMany(s => s.Samples), v => v == OutOfRangeSample);
        Assert.NotNull(reopened.ReadPathology("narrow"));
    }

    [Fact]
    public void In_memory_export_reopens_with_every_record()
    {
        var packBytes = ContentPackWriter.BuildEncryptedPack(NewOverlay());
        using var reopened = EncryptedArchive.OpenBytes(packBytes);
        var names = reopened.EntryPaths.ToList();

        Assert.Contains("manifest.txt", names);
        Assert.Contains("wide.dat", names);
        Assert.Contains("narrow.dat", names);
    }

    [Fact]
    public void Export_still_excludes_tombstones_and_includes_overlay_edits()
    {
        var ov = NewOverlay();
        var created = ov.CreatePathology("Made Up", null, 400, 1024)!; // fresh overlay record (in range)
        ov.DeletePathology("narrow");

        var packBytes = ContentPackWriter.BuildEncryptedPack(ov);
        using var reopened = EncryptedArchive.OpenBytes(packBytes);
        var names = reopened.EntryPaths.ToList();

        Assert.Contains("wide.dat", names);          // untouched base record passes through
        Assert.Contains($"{created}.dat", names);    // overlay-created record included
        Assert.DoesNotContain("narrow.dat", names);  // tombstoned record excluded
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void WriteBasePakWithWideRangeRecord(string path)
    {
        // "wide" carries one sample above the 16-bit signed range, stored as TEXT (the format that can
        // hold it — the same shape a WFDB import lands in). "narrow" is an ordinary in-range record.
        var entries = new[]
        {
            new PathologyEntry("wide", "Wide Range", null, Leads.All.Count, "wide.dat"),
            new PathologyEntry("narrow", "Narrow", null, Leads.All.Count, "narrow.dat"),
        };
        var manifest = new PathologyManifest("1.0", 1024, Leads.All, entries.ToList());

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddText(zip, "manifest.txt", PathologyParser.SerializeManifest(manifest));
                AddText(zip, "wide.dat", PathologyParser.SerializePathology(MakeRecord("wide", OutOfRangeSample), Leads.All));
                AddText(zip, "narrow.dat", PathologyParser.SerializePathology(MakeRecord("narrow", 500), Leads.All));
            }
            zipBytes = ms.ToArray();
        }
        File.WriteAllBytes(path, ContentCrypto.Encrypt(zipBytes));
    }

    private static PathologyFile MakeRecord(string id, int peakSample)
    {
        var samples = new int[512];
        Array.Fill(samples, 1024);
        samples[10] = peakSample; // one spike; for "wide" this is beyond the 16-bit range
        var leads = Leads.All.ToDictionary(l => l, l => new LeadStream(l, (int[])samples.Clone()));
        return new PathologyFile(id, id + " title", null, leads);
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
