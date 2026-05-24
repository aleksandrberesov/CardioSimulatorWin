using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class FilePathologySourceTests : IDisposable
{
    private readonly string _dir;

    public FilePathologySourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardio_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WritePathology_ThenRead_RoundTrips()
    {
        var file = new PathologyFile("p", "Title", "Имя", new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
            [Lead.II] = new LeadStream(Lead.II, new[] { 1024, 824 }),
        });
        var source = new FilePathologySource(_dir);

        Assert.True(source.WritePathology(file, Leads.All));

        var read = source.ReadPathology("p");
        Assert.NotNull(read);
        Assert.Equal("Title", read!.TitleEn);
        Assert.Equal("Имя", read.NameRu);
        Assert.Equal(file.Leads[Lead.I], read.Leads[Lead.I]);
        Assert.Equal(file.Leads[Lead.II], read.Leads[Lead.II]);
    }

    [Fact]
    public void IsValid_RequiresManifest()
    {
        var source = new FilePathologySource(_dir);
        Assert.False(source.IsValid());

        File.WriteAllText(Path.Combine(_dir, "manifest.txt"),
            "version:1.0\nbaseline:1024\nlead_order:I,II\n\n");
        Assert.True(source.IsValid());
    }

    [Fact]
    public void ListPathologies_ReturnsDatBasenames()
    {
        File.WriteAllText(Path.Combine(_dir, "a.dat"), "pathology:a\ntitle:t\nname:n\nleads:0\n");
        File.WriteAllText(Path.Combine(_dir, "b.dat"), "pathology:b\ntitle:t\nname:n\nleads:0\n");
        File.WriteAllText(Path.Combine(_dir, "manifest.txt"), "version:1.0\n");

        var ids = new FilePathologySource(_dir).ListPathologies().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "a", "b" }, ids);
    }
}

/// <summary>
/// Integration smoke test against the real dataset, if present on this machine.
/// Skipped (passes trivially) when the dataset folder is absent.
/// </summary>
public class DatasetSmokeTests
{
    private const string DatasetDir = @"C:\VLN_Project\Data\Data\Pathologies";

    [Fact]
    public void RealDataset_LoadsManifestAndPathologies()
    {
        if (!Directory.Exists(DatasetDir)) return; // dataset not available here

        var repo = new PathologyRepository(new FilePathologySource(DatasetDir));
        Assert.True(repo.LoadManifest());

        var entries = repo.Pathologies();
        Assert.True(entries.Count >= 50, $"expected ≥50 pathologies, got {entries.Count}");

        // A known 12-lead pathology renders lead II.
        var tachpm = repo.ReadPathology("tachpm");
        Assert.NotNull(tachpm);
        Assert.Equal(12, tachpm!.Leads.Count);
        Assert.NotNull(repo.LeadWaveform("tachpm", Lead.II));

        // emd ships only 6 limb leads; precordial leads cannot be synthesized.
        var emd = repo.ReadPathology("emd");
        Assert.NotNull(emd);
        Assert.Equal(6, emd!.Leads.Count);
        Assert.Null(repo.LeadWaveform("emd", Lead.V1));
    }
}
