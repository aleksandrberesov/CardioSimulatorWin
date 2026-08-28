using System.Text;
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

    // C2 (customer 28-08): the doctor-verification status persists in the .dat header (verify:) and
    // round-trips; an academic rhythm (no clinical case) reads as Verified by default even when unset.
    [Fact]
    public void SerializeThenParse_RoundTripsVerificationStatus()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }, System.Array.Empty<EcgElementInstance>()),
        };
        var file = new PathologyFile("v1", "T", "Т", leads) { Verification = VerificationStatus.InReview };

        var round = PathologyParser.ParsePathology(PathologyParser.SerializePathology(file, Leads.All));

        Assert.Equal(VerificationStatus.InReview, round.Verification);
        Assert.Equal(VerificationStatus.InReview, round.EffectiveVerification);
    }

    [Fact]
    public void EffectiveVerification_AcademicRhythm_DefaultsToVerified()
    {
        var academic = new PathologyEntry("a", "T", null, 12, "a.dat");
        var clinical = new PathologyEntry("c", "T", null, 12, "c.dat", ClinicalCase: "age=60");

        Assert.Equal(VerificationStatus.Verified, academic.EffectiveVerification);
        Assert.Equal(VerificationStatus.Unchecked, clinical.EffectiveVerification);
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
    public void ParsePathology_ReadsDescription()
    {
        var text =
            "pathology:test\n" +
            "title:Test Pathology\n" +
            "name:Тест\n" +
            "description:This is a multiline\\ndescription test.\n" +
            "leads:1\n\n" +
            "lead:I\n" +
            "count:3\n" +
            "points:1024,1124,924\n";

        var file = PathologyParser.ParsePathology(text);
        Assert.Equal("test", file.Id);
        Assert.Equal("This is a multiline\ndescription test.", file.Description);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsDescription()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
        };
        var file = new PathologyFile("test", "T", "Т", leads)
        {
            Description = "This is a multiline\ndescription test."
        };

        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.Contains("description:This is a multiline\\ndescription test.", text);

        var reparsed = PathologyParser.ParsePathology(text);
        Assert.Equal("This is a multiline\ndescription test.", reparsed.Description);
    }

    [Fact]
    public void ParsePathology_ReadsNumber()
    {
        var text =
            "pathology:test\n" +
            "title:Test Pathology\n" +
            "number:7\n" +
            "name:Тест\n" +
            "leads:1\n\n" +
            "lead:I\n" +
            "count:3\n" +
            "points:1024,1124,924\n";

        var file = PathologyParser.ParsePathology(text);
        Assert.Equal("test", file.Id);
        Assert.Equal(7, file.Number);
    }

    [Fact]
    public void ParsePathology_NoNumber_IsNull()
    {
        var file = PathologyParser.ParsePathology(DatText);
        Assert.Null(file.Number);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsNumber()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
        };
        var file = new PathologyFile("test", "T", "Т", leads) { Number = 12 };

        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.Contains("number:12", text);

        var reparsed = PathologyParser.ParsePathology(text);
        Assert.Equal(12, reparsed.Number);
    }

    [Fact]
    public void ParseManifest_ReadsAndSerializesNumber()
    {
        var manifestText =
            "version:1.0\n" +
            "baseline:1024\n" +
            "lead_order:I,II\n" +
            "pathologies:1\n" +
            "\n" +
            "pathology:tachpm;leads:12;title:Atrial tachycardia;number:3\n";

        var manifest = PathologyParser.ParseManifest(manifestText);
        Assert.Single(manifest.Entries);
        Assert.Equal(3, manifest.Entries[0].Number);

        var serialized = PathologyParser.SerializeManifest(manifest);
        Assert.Contains(";number:3", serialized);

        var reparsed = PathologyParser.ParseManifest(serialized);
        Assert.Equal(3, reparsed.Entries[0].Number);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsTips()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
        };
        var tips = new List<TipOverlay>
        {
            new(TipOverlayKind.Arrow, new[] { new TipPoint(10f, 1200f), new TipPoint(30f, 1024f) },
                Text: "ST elevation | note ~ here"),
            new(TipOverlayKind.LeadArea, new[] { new TipPoint(5f, 1024f) }, Lead: Lead.aVL),
            new(TipOverlayKind.VerticalLines, new[] { new TipPoint(15f, 1024f) }, EndCap: TipLineEndCap.Arrows),
            new(TipOverlayKind.Points, new[] { new TipPoint(1f, 1100f), new TipPoint(2f, 900f) }),
        };
        var file = new PathologyFile("test", "T", "Т", leads) { Tips = tips };

        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.Contains("tips:", text);
        // A tips value must never wrap the single header line.
        var tipsLine = text.Split('\n').Single(l => l.StartsWith("tips:"));
        Assert.DoesNotContain("\n", tipsLine[5..]);

        var reparsed = PathologyParser.ParsePathology(text);
        Assert.Equal(4, reparsed.Tips.Count);

        var arrow = reparsed.Tips[0];
        Assert.Equal(TipOverlayKind.Arrow, arrow.Kind);
        Assert.Equal(2, arrow.Points.Count);
        Assert.Equal(10f, arrow.Points[0].Sample, 3);
        Assert.Equal(1200f, arrow.Points[0].Adc, 3);
        Assert.Equal("ST elevation | note ~ here", arrow.Text); // reserved chars survive escaping

        Assert.Equal(Lead.aVL, reparsed.Tips[1].Lead);
        Assert.Equal(TipLineEndCap.Arrows, reparsed.Tips[2].EndCap);
        Assert.Equal(2, reparsed.Tips[3].Points.Count);
    }

    [Fact]
    public void SerializeThenParse_RoundTripsTipComments()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924 }),
        };
        var file = new PathologyFile("test", "T", "Т", leads)
        {
            TipComments = new[] { "ST elevation in II, III, aVF", "Reciprocal changes | see aVL ~ I" },
        };

        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.Contains("tip_notes:", text);
        Assert.DoesNotContain("\n", text.Split('\n').Single(l => l.StartsWith("tip_notes:"))[10..]);

        var reparsed = PathologyParser.ParsePathology(text);
        Assert.Equal(2, reparsed.TipComments.Count);
        Assert.Equal("ST elevation in II, III, aVF", reparsed.TipComments[0]);
        Assert.Equal("Reciprocal changes | see aVL ~ I", reparsed.TipComments[1]); // reserved chars survive
    }

    [Fact]
    public void ParsePathology_NoTips_EmptyList()
    {
        var file = PathologyParser.ParsePathology(DatText);
        Assert.Empty(file.Tips);
    }

    [Fact]
    public void SerializePathology_NoTips_OmitsField()
    {
        var file = PathologyParser.ParsePathology(DatText);
        var text = PathologyParser.SerializePathology(file, Leads.All);
        Assert.DoesNotContain("tips:", text);
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

    // ─── delta-binary (CSD1) format ─────────────────────────────────────

    [Fact]
    public void SerializePathologyBytes_StartsWithCsd1Magic()
    {
        var file = PathologyParser.ParsePathology(DatText);
        var bytes = PathologyParser.SerializePathologyBytes(file, Leads.All);

        Assert.True(bytes.Length > 5);
        Assert.Equal(new byte[] { (byte)'C', (byte)'S', (byte)'D', (byte)'1' }, bytes[..4]);
    }

    [Fact]
    public void ParsePathology_Bytes_ParsesPlainTextUnchanged()
    {
        var file = PathologyParser.ParsePathology(Encoding.UTF8.GetBytes(DatText));

        Assert.Equal("test", file.Id);
        Assert.Equal(2, file.Leads.Count);
        Assert.Equal(new[] { 1024, 1124, 924 }, file.Leads[Lead.I].Samples);
        Assert.Equal(new[] { 1024, 1024, 1224, 824 }, file.Leads[Lead.II].Samples);
    }

    [Fact]
    public void ParsePathology_Bytes_ToleratesUtf8Bom()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var bytes = bom.Concat(Encoding.UTF8.GetBytes(DatText)).ToArray();

        var file = PathologyParser.ParsePathology(bytes);
        Assert.Equal("test", file.Id);
    }

    [Fact]
    public void SerializeBytesThenParse_RoundTripsLeadsAndHeader()
    {
        var original = PathologyParser.ParsePathology(DatText);

        var bytes = PathologyParser.SerializePathologyBytes(original, Leads.All);
        var reparsed = PathologyParser.ParsePathology(bytes);

        Assert.Equal(original.Id, reparsed.Id);
        Assert.Equal(original.TitleEn, reparsed.TitleEn);
        Assert.Equal(original.NameRu, reparsed.NameRu);
        Assert.Equal(original.Leads.Count, reparsed.Leads.Count);
        Assert.Equal(original.Leads[Lead.I], reparsed.Leads[Lead.I]);
        Assert.Equal(original.Leads[Lead.II], reparsed.Leads[Lead.II]);
    }

    [Fact]
    public void SerializeBytesThenParse_RoundTripsAllMetadata()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.I] = new LeadStream(Lead.I, new[] { 1024, 1124, 924, 1000 }, new[]
            {
                new EcgElementInstance(EcgElement.TWave, 1, 2, 0.3f),
            }),
            [Lead.aVR] = new LeadStream(Lead.aVR, new[] { 1024, 1024, 1024 }),
        };
        var file = new PathologyFile("test", "Test", "Тест", leads)
        {
            Group = "ischemia",
            ClinicalCase = "age=60,gender=Female,hr=80,bp=130/85",
            Number = 12,
            Description = "Line one\nline two",
            SignificantPoints = new[]
            {
                new SignificantPoint(0, EcgPointType.P_PEAK),
                new SignificantPoint(2, EcgPointType.R_PEAK),
            },
            Tips = new[]
            {
                new TipOverlay(TipOverlayKind.Arrow, new[] { new TipPoint(10f, 1200f) }, Text: "ST | note ~ x"),
            },
            TipComments = new[] { "ST elevation", "recip | changes ~ aVL" },
        };

        var bytes = PathologyParser.SerializePathologyBytes(file, Leads.All);
        var r = PathologyParser.ParsePathology(bytes);

        Assert.Equal("Test", r.TitleEn);
        Assert.Equal("Тест", r.NameRu);
        Assert.Equal("ischemia", r.Group);
        Assert.Equal("age=60,gender=Female,hr=80,bp=130/85", r.ClinicalCase);
        Assert.Equal(12, r.Number);
        Assert.Equal("Line one\nline two", r.Description);
        Assert.Equal(2, r.SignificantPoints.Count);
        Assert.Equal(file.SignificantPoints[0], r.SignificantPoints[0]);
        Assert.Equal(file.SignificantPoints[1], r.SignificantPoints[1]);
        Assert.Equal(2, r.Leads.Count);
        Assert.Equal(file.Leads[Lead.I], r.Leads[Lead.I]); // samples + elements
        Assert.Equal(file.Leads[Lead.aVR], r.Leads[Lead.aVR]);
        Assert.Single(r.Tips);
        Assert.Equal("ST | note ~ x", r.Tips[0].Text);
        Assert.Equal(2, r.TipComments.Count);
        Assert.Equal("recip | changes ~ aVL", r.TipComments[1]);
    }

    [Fact]
    public void SerializePathologyBytes_WritesOnlyPresentLeads()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.II] = new LeadStream(Lead.II, new[] { 1024, 1030, 1010 }),
        };
        var file = new PathologyFile("x", "X", "Х", leads);

        var bytes = PathologyParser.SerializePathologyBytes(file, Leads.All);
        var r = PathologyParser.ParsePathology(bytes);

        Assert.Single(r.Leads);
        Assert.True(r.Leads.ContainsKey(Lead.II));
        Assert.Equal(new[] { 1024, 1030, 1010 }, r.Leads[Lead.II].Samples);
    }

    [Fact]
    public void SerializeBytesThenParse_ReconstructsAcrossInt16DeltaOverflow()
    {
        // Consecutive deltas here exceed the 16-bit range; two's-complement wrap-around must still
        // reconstruct every sample exactly since each sample itself fits in an int16.
        var samples = new[] { 30000, -30000, 30000, 0, 32767, -32768 };
        var leads = new Dictionary<Lead, LeadStream> { [Lead.I] = new LeadStream(Lead.I, samples) };
        var file = new PathologyFile("x", "X", "Х", leads);

        var bytes = PathologyParser.SerializePathologyBytes(file, Leads.All);
        var r = PathologyParser.ParsePathology(bytes);

        Assert.Equal(samples, r.Leads[Lead.I].Samples);
    }

    [Fact]
    public void SerializePathologyBytes_SampleOutOfInt16Range_Throws()
    {
        var leads = new Dictionary<Lead, LeadStream> { [Lead.I] = new LeadStream(Lead.I, new[] { 40000 }) };
        var file = new PathologyFile("x", "X", "Х", leads);

        Assert.Throws<PathologyFormatException>(
            () => PathologyParser.SerializePathologyBytes(file, Leads.All));
    }
}
