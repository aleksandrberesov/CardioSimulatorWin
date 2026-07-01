using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class PathologyParserTests
{
    private const string ManifestText =
        "version:1.0\n" +
        "baseline:1024\n" +
        "lead_order:I,II,III,aVR,aVL,aVF,V1,V2,V3,V4,V5,V6\n" +
        "pathologies:2\n" +
        "\n" +
        "pathology:tachpm;leads:12;samples:31568;title:Atrial tachycardia\n" +
        "pathology:emd;leads:6;samples:2412;title:Electromechanical dissociation (EMD)\n";

    private const string DatText =
        "pathology:test\n" +
        "title:Test Pathology\n" +
        "name:Тест\n" +
        "leads:2\n" +
        "\n" +
        "lead:I\n" +
        "count:3\n" +
        "points:1024,1124,924\n" +
        "\n" +
        "lead:II\n" +
        "count:4\n" +
        "points:1024,1024,1224,824\n";

    private const string DatTextWithMarkers =
        "pathology:test\n" +
        "title:Test Pathology\n" +
        "name:Тест\n" +
        "leads:1\n" +
        "markers:0:P_PEAK,2:R_PEAK\n" +
        "\n" +
        "lead:I\n" +
        "count:3\n" +
        "points:1024,1124,924\n";

    [Fact]
    public void ParsePathology_ReadsElementsAnnotation()
    {
        const string text =
            "pathology:test\ntitle:T\nname:Т\nleads:1\n\n" +
            "lead:I\ncount:3\npoints:1024,1124,924\n" +
            "elements:PWave:0:45:0.15,QrsComplex:60:45:1\n";

        var file = PathologyParser.ParsePathology(text);
        var elements = file.Leads[Lead.I].Elements;

        Assert.Equal(2, elements.Count);
        Assert.Equal(EcgElement.PWave, elements[0].Type);
        Assert.Equal(0, elements[0].StartIndex);
        Assert.Equal(45, elements[0].Length);
        Assert.Equal(0.15f, elements[0].AmplitudeMv, 3);
        Assert.Equal(EcgElement.QrsComplex, elements[1].Type);
        Assert.Equal(60, elements[1].StartIndex);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsElements()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }, new[]
            {
                new EcgElementInstance(EcgElement.TWave, 1, 2, 0.3f),
            }),
        };
        var file = new PathologyFile("test", "T", "Т", leads);

        var round = PathologyParser.ParsePathology(PathologyParser.SerializePathology(file, Leads.All));
        var elements = round.Leads[Lead.I].Elements;

        Assert.Single(elements);
        Assert.Equal(EcgElement.TWave, elements[0].Type);
        Assert.Equal(1, elements[0].StartIndex);
        Assert.Equal(2, elements[0].Length);
        Assert.Equal(0.3f, elements[0].AmplitudeMv, 3);
    }

    [Fact]
    public void ParseManifest_ReadsHeaderAndEntries()
    {
        var manifest = PathologyParser.ParseManifest(ManifestText);

        Assert.Equal("1.0", manifest.Version);
        Assert.Equal(1024, manifest.Baseline);
        Assert.Equal(12, manifest.LeadOrder.Count);
        Assert.Equal(Lead.I, manifest.LeadOrder[0]);
        Assert.Equal(Lead.V6, manifest.LeadOrder[11]);
        Assert.Equal(2, manifest.Entries.Count);

        var tachpm = manifest.Entries[0];
        Assert.Equal("tachpm", tachpm.Id);
        Assert.Equal("Atrial tachycardia", tachpm.TitleEn);
        Assert.Null(tachpm.NameRu);
        Assert.Equal(12, tachpm.LeadsCount);
        Assert.Equal("tachpm.dat", tachpm.FileName);

        Assert.Equal(6, manifest.Entries[1].LeadsCount);
    }

    [Fact]
    public void ParseManifest_MissingVersion_Throws()
    {
        var text = "baseline:1024\nlead_order:I,II\n\n";
        var ex = Assert.Throws<PathologyFormatException>(() => PathologyParser.ParseManifest(text));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void ParseManifest_UnsupportedVersion_Throws()
    {
        var text = "version:2.0\nbaseline:1024\nlead_order:I,II\n\n";
        Assert.Throws<PathologyFormatException>(() => PathologyParser.ParseManifest(text));
    }

    [Fact]
    public void ParseManifest_RoundTrips()
    {
        var original = PathologyParser.ParseManifest(ManifestText);
        var reparsed = PathologyParser.ParseManifest(PathologyParser.SerializeManifest(original));

        Assert.Equal(original.Version, reparsed.Version);
        Assert.Equal(original.Baseline, reparsed.Baseline);
        Assert.True(original.LeadOrder.SequenceEqual(reparsed.LeadOrder));
        Assert.True(original.Entries.SequenceEqual(reparsed.Entries));
    }

    [Fact]
    public void ParsePathology_ReadsHeaderAndLeads()
    {
        var file = PathologyParser.ParsePathology(DatText);

        Assert.Equal("test", file.Id);
        Assert.Equal("Test Pathology", file.TitleEn);
        Assert.Equal("Тест", file.NameRu);
        Assert.Equal(2, file.Leads.Count);
        Assert.Equal(new[] { 1024, 1124, 924 }, file.Leads[Lead.I].Samples);
        Assert.Equal(new[] { 1024, 1024, 1224, 824 }, file.Leads[Lead.II].Samples);
    }

    [Fact]
    public void ParsePathology_RoundTrips()
    {
        var original = PathologyParser.ParsePathology(DatText);
        var text = PathologyParser.SerializePathology(original, Leads.All);
        var reparsed = PathologyParser.ParsePathology(text);

        Assert.Equal(original.Id, reparsed.Id);
        Assert.Equal(original.TitleEn, reparsed.TitleEn);
        Assert.Equal(original.NameRu, reparsed.NameRu);
        Assert.Equal(original.Leads.Count, reparsed.Leads.Count);
        Assert.Equal(original.Leads[Lead.I], reparsed.Leads[Lead.I]);
        Assert.Equal(original.Leads[Lead.II], reparsed.Leads[Lead.II]);
    }

    [Fact]
    public void ParsePathology_UnknownLead_Throws()
    {
        var text = "pathology:x\ntitle:t\nname:n\nleads:1\n\nlead:ZZ\ncount:1\npoints:1024\n";
        var ex = Assert.Throws<PathologyFormatException>(() => PathologyParser.ParsePathology(text));
        Assert.Contains("unknown lead", ex.Message);
    }

    [Fact]
    public void ParsePathology_CountMismatch_Throws()
    {
        var text = "pathology:x\ntitle:t\nname:n\nleads:1\n\nlead:I\ncount:3\npoints:1,2\n";
        Assert.Throws<PathologyFormatException>(() => PathologyParser.ParsePathology(text));
    }

    [Fact]
    public void ParsePathology_SkipsNonIntegerSamples_ProducingCountMismatch()
    {
        // parseIntCsv drops "x"; parsed length (2) != declared count (3) → throw.
        var text = "pathology:x\ntitle:t\nname:n\nleads:1\n\nlead:I\ncount:3\npoints:1,x,3\n";
        Assert.Throws<PathologyFormatException>(() => PathologyParser.ParsePathology(text));
    }

    [Fact]
    public void ParsePathology_ReadsMarkers()
    {
        var file = PathologyParser.ParsePathology(DatTextWithMarkers);

        Assert.Equal(2, file.SignificantPoints.Count);
        Assert.Equal(new SignificantPoint(0, EcgPointType.P_PEAK), file.SignificantPoints[0]);
        Assert.Equal(new SignificantPoint(2, EcgPointType.R_PEAK), file.SignificantPoints[1]);
    }

    [Fact]
    public void ParsePathology_NoMarkers_EmptyList()
    {
        var file = PathologyParser.ParsePathology(DatText);
        Assert.Empty(file.SignificantPoints);
    }

    [Fact]
    public void SerializePathology_RoundTripsMarkers()
    {
        var original = PathologyParser.ParsePathology(DatTextWithMarkers);
        var text = PathologyParser.SerializePathology(original, Leads.All);
        var reparsed = PathologyParser.ParsePathology(text);

        Assert.Contains("markers:0:P_PEAK,2:R_PEAK", text);
        Assert.True(original.SignificantPoints.SequenceEqual(reparsed.SignificantPoints));
    }

    [Fact]
    public void ParsePathology_SkipsUnknownMarkerType()
    {
        var text =
            "pathology:x\ntitle:t\nname:n\nleads:1\n" +
            "markers:1:NOPE,3:T_PEAK\n\n" +
            "lead:I\ncount:4\npoints:1,2,3,4\n";

        var file = PathologyParser.ParsePathology(text);

        Assert.Single(file.SignificantPoints);
        Assert.Equal(new SignificantPoint(3, EcgPointType.T_PEAK), file.SignificantPoints[0]);
    }

    [Fact]
    public void ParsePathology_ReadsClinicalCase()
    {
        var text =
            "pathology:test\n" +
            "title:Test Pathology\n" +
            "name:Тест\n" +
            "group:sinus\n" +
            "clinical_case:age=45,gender=Male,hr=72,bp=120/80\n" +
            "leads:1\n\n" +
            "lead:I\n" +
            "count:3\n" +
            "points:1024,1124,924\n";

        var file = PathologyParser.ParsePathology(text);
        Assert.Equal("test", file.Id);
        Assert.Equal("sinus", file.Group);
        Assert.Equal("age=45,gender=Male,hr=72,bp=120/80", file.ClinicalCase);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsClinicalCase()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
        };
        var file = new PathologyFile("test", "T", "Т", leads)
        {
            Group = "ischemia",
            ClinicalCase = "age=60,gender=Female,hr=80,bp=130/85"
        };

        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.Contains("group:ischemia", text);
        Assert.Contains("clinical_case:age=60,gender=Female,hr=80,bp=130/85", text);

        var reparsed = PathologyParser.ParsePathology(text);
        Assert.Equal("ischemia", reparsed.Group);
        Assert.Equal("age=60,gender=Female,hr=80,bp=130/85", reparsed.ClinicalCase);
    }

    [Fact]
    public void ParseManifest_ReadsClinicalCase()
    {
        var manifestText =
            "version:1.0\n" +
            "baseline:1024\n" +
            "lead_order:I,II\n" +
            "pathologies:1\n" +
            "\n" +
            "pathology:tachpm;leads:12;title:Atrial tachycardia;group:sinus;clinical_case:age=45,gender=Male,hr=72,bp=120/80\n";

        var manifest = PathologyParser.ParseManifest(manifestText);
        Assert.Single(manifest.Entries);
        var entry = manifest.Entries[0];
        Assert.Equal("tachpm", entry.Id);
        Assert.Equal("sinus", entry.Group);
        Assert.Equal("age=45,gender=Male,hr=72,bp=120/80", entry.ClinicalCase);

        var serialized = PathologyParser.SerializeManifest(manifest);
        Assert.Contains(";clinical_case:age=45,gender=Male,hr=72,bp=120/80", serialized);
    }
}
