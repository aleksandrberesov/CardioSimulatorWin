using CardioSimulator.Core.Data;
using CardioSimulator.Core.Data.Wfdb;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>End-to-end Core import: WFDB record → pathology → on-disk dataset.</summary>
public class WfdbImportTests : IDisposable
{
    private readonly string _dir;

    public WfdbImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardio_import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static WfdbRecord TwoLeadRecord()
    {
        var specs = new List<WfdbSignalSpec>
        {
            new() { FileName = "r.dat", Format = 16, Gain = 1000, Units = "mV", Description = "I" },
            new() { FileName = "r.dat", Format = 16, Gain = 1000, Units = "mV", Description = "II" },
        };
        var samples = new[]
        {
            new[] { 1000, 0, -1000 },  // +1 / 0 / -1 mV
            new[] { 500, 500, 500 },   // +0.5 mV flat
        };
        var header = new WfdbHeader
        {
            RecordName = "JS99999",
            NumberOfSignals = 2,
            SamplingFrequency = 500,
            NumberOfSamples = 3,
            Signals = specs,
        };
        return new WfdbRecord(header, samples);
    }

    private void SeedManifest()
    {
        var seed = new PathologyManifest("1.0", 1024, Leads.All, Array.Empty<PathologyEntry>());
        File.WriteAllText(Path.Combine(_dir, "manifest.txt"), PathologyParser.SerializeManifest(seed));
    }

    [Fact]
    public void ImportPathology_WritesDatAndManifestEntry_AndReadsBack()
    {
        SeedManifest();
        var source = new FilePathologySource(_dir);
        var pathology = WfdbConverter.ToPathologyFile(TwoLeadRecord(), "JS99999", "Imported record");

        var newId = source.ImportPathology(pathology);

        Assert.NotNull(newId);
        Assert.True(File.Exists(Path.Combine(_dir, newId + ".dat")));

        var read = source.ReadPathology(newId!);
        Assert.NotNull(read);
        Assert.Equal("Imported record", read!.TitleEn);
        // +1 mV => 1024 + 256; 0 mV => 1024; -1 mV => 1024 - 256.
        Assert.Equal(new[] { 1280, 1024, 768 }, read.Leads[Lead.I].Samples);
        Assert.Equal(new[] { 1152, 1152, 1152 }, read.Leads[Lead.II].Samples);

        var manifest = source.ReadManifest();
        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Entries, e => e.Id == newId);
    }

    [Fact]
    public void Repository_ImportPathology_AppearsInPathologyList()
    {
        SeedManifest();
        var repo = new PathologyRepository(new FilePathologySource(_dir));
        repo.LoadManifest();
        var pathology = WfdbConverter.ToPathologyFile(TwoLeadRecord(), "JS99999", "Imported");

        var newId = repo.ImportPathology(pathology);

        Assert.NotNull(newId);
        Assert.Contains(repo.Pathologies(), e => e.Id == newId);
    }

    [Fact]
    public void ImportPathology_TwiceFromSameRecord_GeneratesDistinctIds()
    {
        SeedManifest();
        var source = new FilePathologySource(_dir);
        var pathology = WfdbConverter.ToPathologyFile(TwoLeadRecord(), "JS99999", "Imported");

        var first = source.ImportPathology(pathology);
        var second = source.ImportPathology(pathology);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }
}
