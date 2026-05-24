using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class PathologyRepositoryTests
{
    private sealed class InMemorySource : IPathologySource
    {
        private readonly PathologyManifest _manifest;
        private readonly Dictionary<string, PathologyFile> _files;

        public InMemorySource(PathologyManifest manifest, params PathologyFile[] files)
        {
            _manifest = manifest;
            _files = files.ToDictionary(f => f.Id);
        }

        public PathologyManifest? ReadManifest() => _manifest;
        public PathologyFile? ReadPathology(string id) => _files.GetValueOrDefault(id);
        public IReadOnlyList<string> ListPathologies() => _files.Keys.ToList();
    }

    private static PathologyManifest Manifest(params PathologyEntry[] entries) =>
        new("1.0", 1024, Leads.All, entries);

    private static PathologyEntry Entry(string id, string title) =>
        new(id, title, null, 12, $"{id}.dat");

    private static PathologyFile File(string id, params (Lead lead, int[] samples)[] leads)
    {
        var map = leads.ToDictionary(l => l.lead, l => new LeadStream(l.lead, l.samples));
        return new PathologyFile(id, id, null, map);
    }

    [Fact]
    public void LeadWaveform_DirectLead_ZeroesBaseline()
    {
        var repo = new PathologyRepository(new InMemorySource(
            Manifest(Entry("p", "P")),
            File("p", (Lead.I, new[] { 1024, 1124, 924 }))));
        repo.LoadManifest();

        var points = repo.LeadWaveform("p", Lead.I);

        Assert.NotNull(points);
        Assert.Equal(new[] { 0f, 100f, -100f }, points!.Values);
    }

    [Fact]
    public void LeadWaveform_SynthesizesMissingLimbLead()
    {
        var repo = new PathologyRepository(new InMemorySource(
            Manifest(Entry("p", "P")),
            File("p",
                (Lead.I, new[] { 1024, 1024 }),
                (Lead.II, new[] { 1124, 1224 }))));
        repo.LoadManifest();

        // III = II - I, on baseline-zeroed samples: [100-0, 200-0] = [100, 200]
        var iii = repo.LeadWaveform("p", Lead.III);

        Assert.NotNull(iii);
        Assert.Equal(new[] { 100f, 200f }, iii!.Values);
    }

    [Fact]
    public void LeadWaveform_ReturnsNullWhenNotDerivable()
    {
        var repo = new PathologyRepository(new InMemorySource(
            Manifest(Entry("p", "P")),
            File("p", (Lead.I, new[] { 1024, 1024 }))));
        repo.LoadManifest();

        Assert.Null(repo.LeadWaveform("p", Lead.III)); // needs II
        Assert.Null(repo.LeadWaveform("p", Lead.V1));  // needs V2 + V6
    }

    [Fact]
    public void LeadWaveform_UnknownPathology_ReturnsNull()
    {
        var repo = new PathologyRepository(new InMemorySource(Manifest()));
        Assert.Null(repo.LeadWaveform("missing", Lead.II));
    }

    [Fact]
    public void Pathologies_SortedByTitleCaseInsensitive()
    {
        var repo = new PathologyRepository(new InMemorySource(
            Manifest(Entry("b", "Beta"), Entry("a", "alpha"), Entry("c", "Gamma"))));
        repo.LoadManifest();

        var titles = repo.Pathologies().Select(e => e.TitleEn).ToList();
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, titles);
    }
}
