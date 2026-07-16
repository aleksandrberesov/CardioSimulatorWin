using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Tests for the TCP upload's text-ZIP conversion. The server ingests text, and the pack stores CSD1
/// delta-binary, so this conversion is the whole contract — and its failure mode is silent: a zip full
/// of binary <c>.dat</c> bytes is still a perfectly valid zip that the server would simply misread.
/// </summary>
public class PlainTextZipWriterTests
{
    private sealed class FakeSource : IContentPackExportable
    {
        private readonly List<KeyValuePair<string, byte[]>> _entries = new();
        public void Add(string name, byte[] bytes) => _entries.Add(new(name, bytes));
        public IEnumerable<KeyValuePair<string, byte[]>> ExportEntries() => _entries;
    }

    private static readonly IReadOnlyList<Lead> Order = new[] { Lead.I, Lead.II };

    private static PathologyFile SampleFile(string id = "afib") => new(
        id,
        "Atrial fibrillation",
        "Фибрилляция предсердий",
        new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new(Lead.I, new[] { 1024, 1030, 1010, 990, 1024 }),
            [Lead.II] = new(Lead.II, new[] { 1024, 1100, 900, 1024 }),
        });

    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result[entry.FullName] = ms.ToArray();
        }
        return result;
    }

    private static Dictionary<string, byte[]> WriteAndRead(FakeSource source)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cardio-test-{Guid.NewGuid():N}.zip");
        try
        {
            PlainTextZipWriter.WriteTextZip(source, Order, tmp);
            return ReadZip(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void DecodesBinaryDatBackToText()
    {
        var original = SampleFile();
        var source = new FakeSource();
        source.Add("afib.dat", PathologyParser.SerializePathologyBytes(original, Order));

        var entry = WriteAndRead(source)["afib.dat"];

        // The point of the whole exercise: what lands in the zip must be text, not CSD1.
        Assert.False(PathologyParser.HasBinaryMagic(entry));
        Assert.StartsWith("pathology:", Encoding.UTF8.GetString(entry));
    }

    [Fact]
    public void BinaryToTextPreservesSamplesExactly()
    {
        var original = SampleFile();
        var source = new FakeSource();
        source.Add("afib.dat", PathologyParser.SerializePathologyBytes(original, Order));

        var reparsed = PathologyParser.ParsePathology(WriteAndRead(source)["afib.dat"]);

        Assert.Equal(original.Id, reparsed.Id);
        Assert.Equal(original.TitleEn, reparsed.TitleEn);
        Assert.Equal(original.NameRu, reparsed.NameRu);
        Assert.Equal(original.Leads.Keys.OrderBy(l => l), reparsed.Leads.Keys.OrderBy(l => l));
        foreach (var (lead, stream) in original.Leads)
        {
            Assert.Equal(stream.Samples, reparsed.Leads[lead].Samples);
        }
    }

    [Fact]
    public void PassesAlreadyTextEntriesThroughVerbatim()
    {
        // An overlay .dat saved as text, plus the text sidecars, must not be rewritten.
        var text = Encoding.UTF8.GetBytes(PathologyParser.SerializePathology(SampleFile(), Order));
        var manifest = Encoding.UTF8.GetBytes("version:1.0\nbaseline:1024\n");
        var groups = Encoding.UTF8.GetBytes("conduction=Conduction\n");

        var source = new FakeSource();
        source.Add("edited.dat", text);
        source.Add("manifest.txt", manifest);
        source.Add("groups.txt", groups);

        var zip = WriteAndRead(source);

        Assert.Equal(text, zip["edited.dat"]);
        Assert.Equal(manifest, zip["manifest.txt"]);
        Assert.Equal(groups, zip["groups.txt"]);
    }

    [Fact]
    public void ProducesAPlainZipNotAPack()
    {
        // Regression guard for the actual switch: the old path wrote an encrypted pack here.
        var source = new FakeSource();
        source.Add("afib.dat", PathologyParser.SerializePathologyBytes(SampleFile(), Order));

        var tmp = Path.Combine(Path.GetTempPath(), $"cardio-test-{Guid.NewGuid():N}.zip");
        try
        {
            PlainTextZipWriter.WriteTextZip(source, Order, tmp);
            var raw = File.ReadAllBytes(tmp);

            Assert.False(ContentCrypto.LooksLikePack(raw));
            Assert.Equal(new byte[] { (byte)'P', (byte)'K' }, raw[..2]); // local file header
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void LeavesNoTempFileBehind()
    {
        var source = new FakeSource();
        source.Add("afib.dat", PathologyParser.SerializePathologyBytes(SampleFile(), Order));

        var tmp = Path.Combine(Path.GetTempPath(), $"cardio-test-{Guid.NewGuid():N}.zip");
        try
        {
            PlainTextZipWriter.WriteTextZip(source, Order, tmp);
            Assert.False(File.Exists(tmp + ".tmp"));
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }
}
