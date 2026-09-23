using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// The bundled OSCE dataset: a pack read in memory (<see cref="EncryptedOskeSource"/>) with the
/// instructor's own forms and answer keys layered over it (<see cref="CompositeOskeSource"/>).
/// </summary>
public class BundledOskeTests : IDisposable
{
    private readonly string _dir;

    public BundledOskeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oske_pak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static OskeForm Form(string formId, OskeSpecialty specialty, string title = "Rhythm") => new(
        formId, specialty, "2025.05",
        new List<OskeQuestion>
        {
            new("rhythm", 1, title, OskeAnswerKind.Single,
                new List<OskeOption> { new("sinus", "Sinus"), new("afib", "AFib") }),
        });

    private static OskeAnswerKey Key(string ecgId, string formId, string correct = "sinus") => new(
        ecgId, formId,
        new Dictionary<string, IReadOnlyList<string>> { ["rhythm"] = new List<string> { correct } });

    /// <summary>Builds a pack the way tools/OskePacker does: the file layout, verbatim.</summary>
    private string WritePack(IEnumerable<OskeForm> forms, IEnumerable<OskeAnswerKey> keys, string name = "Oske.pak")
    {
        var path = Path.Combine(_dir, name);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true);
            using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var f in forms) Add(zip, "forms/" + f.FormId + ".json", OskeJson.SerializeForm(f));
            foreach (var k in keys) Add(zip, "answers/" + k.EcgId + "/" + k.FormId + ".json", OskeJson.SerializeAnswerKey(k));
        }
        return path;

        static void Add(ZipArchive zip, string entryPath, string text)
        {
            var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }
    }

    private FileOskeSource OwnSource()
    {
        var dir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(dir);
        return new FileOskeSource(dir);
    }

    [Fact]
    public void PackedDatasetRoundTrips()
    {
        var path = WritePack(
            new[] { Form("therapy", OskeSpecialty.Therapy), Form("cardiology", OskeSpecialty.Cardiology) },
            new[] { Key("ecg00035", "cardiology") });

        using var src = EncryptedOskeSource.Open(path);

        Assert.True(src.IsValid());
        Assert.Equal(2, src.ReadForms().Count);
        Assert.Equal(OskeSpecialty.Therapy, src.ReadForm("therapy")!.Specialty);
        Assert.Equal(new[] { "ecg00035" }, src.ListAnswerKeyEcgIds("cardiology"));
        Assert.Empty(src.ListAnswerKeyEcgIds("therapy"));
        Assert.Equal("sinus", src.ReadAnswerKey("ecg00035", "cardiology")!.CorrectOptionIds["rhythm"].Single());
        Assert.Null(src.ReadAnswerKey("ecg99999", "cardiology"));
    }

    /// <summary>The point of packing the answer keys: the marking scheme is not readable out of the
    /// shipped file, and nothing is written to the user's data folder for a student to find.</summary>
    [Fact]
    public void AnswerKeysAreNotPlaintextInThePack()
    {
        var path = WritePack(
            new[] { Form("cardiology", OskeSpecialty.Cardiology, "A distinctive block title") },
            new[] { Key("ecg00035", "cardiology") });

        var asText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));

        Assert.DoesNotContain("A distinctive block title", asText, StringComparison.Ordinal);
        Assert.DoesNotContain("ecg00035", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeUnionsBundledAndOwnForms()
    {
        var path = WritePack(new[] { Form("therapy", OskeSpecialty.Therapy) }, Array.Empty<OskeAnswerKey>());
        var own = OwnSource();
        own.WriteForm(Form("mine", OskeSpecialty.FunctionalDiagnostics));

        using var bundled = EncryptedOskeSource.Open(path);
        var composite = new CompositeOskeSource(bundled, own);

        Assert.Equal(
            new[] { "mine", "therapy" },
            composite.ReadForms().Select(f => f.FormId).OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void OwnFormShadowsTheBundledOneWithTheSameId()
    {
        var path = WritePack(new[] { Form("therapy", OskeSpecialty.Therapy, "Shipped title") }, Array.Empty<OskeAnswerKey>());
        var own = OwnSource();
        own.WriteForm(Form("therapy", OskeSpecialty.Therapy, "Corrected title"));

        using var bundled = EncryptedOskeSource.Open(path);
        var composite = new CompositeOskeSource(bundled, own);

        var form = Assert.Single(composite.ReadForms());
        Assert.Equal("Corrected title", form.Questions[0].Title);
        Assert.Equal("Corrected title", composite.ReadForm("therapy")!.Questions[0].Title);
    }

    [Fact]
    public void OwnAnswerKeyShadowsTheBundledOne()
    {
        var path = WritePack(
            new[] { Form("cardiology", OskeSpecialty.Cardiology) },
            new[] { Key("ecg00035", "cardiology", "sinus") });
        var own = OwnSource();
        own.WriteAnswerKey(Key("ecg00035", "cardiology", "afib"));

        using var bundled = EncryptedOskeSource.Open(path);
        var composite = new CompositeOskeSource(bundled, own);

        Assert.Equal("afib", composite.ReadAnswerKey("ecg00035", "cardiology")!.CorrectOptionIds["rhythm"].Single());
        // Shadowed, not duplicated - the start dialog counts these as "N ECGs available".
        Assert.Equal(new[] { "ecg00035" }, composite.ListAnswerKeyEcgIds("cardiology"));
    }

    [Fact]
    public void ListAnswerKeyEcgIdsUnionsBothHalves()
    {
        var path = WritePack(
            new[] { Form("cardiology", OskeSpecialty.Cardiology) },
            new[] { Key("ecg00035", "cardiology") });
        var own = OwnSource();
        own.WriteAnswerKey(Key("ecg00010", "cardiology"));

        using var bundled = EncryptedOskeSource.Open(path);
        var composite = new CompositeOskeSource(bundled, own);

        Assert.Equal(new[] { "ecg00010", "ecg00035" }, composite.ListAnswerKeyEcgIds("cardiology"));
    }

    [Fact]
    public void RepositoryStillWritesThroughACompositeSource()
    {
        var path = WritePack(new[] { Form("therapy", OskeSpecialty.Therapy) }, Array.Empty<OskeAnswerKey>());
        var own = OwnSource();

        using var bundled = EncryptedOskeSource.Open(path);
        var repo = new OskeRepository(new CompositeOskeSource(bundled, own));

        Assert.True(repo.WriteForm(Form("mine", OskeSpecialty.FunctionalDiagnostics)));
        Assert.True(repo.WriteAnswerKey(Key("ecg00010", "therapy")));
        repo.Reload();

        Assert.Equal(2, repo.Forms.Count);
        Assert.NotNull(repo.AnswerKey("ecg00010", "therapy"));
        // The authored content went to disk; the pack holds only what was packed.
        Assert.Single(Directory.GetFiles(Path.Combine(own.Root, "forms"), "*.json"));
        Assert.True(File.Exists(Path.Combine(own.Root, "answers", "ecg00010", "therapy.json")));
    }

    /// <summary>A pack with no form entry (e.g. the wrong pack in the slot) reads as empty instead of
    /// taking the app down; AppViewModel then falls back to the disk dataset.</summary>
    [Fact]
    public void PackWithoutFormsReadsAsEmpty()
    {
        var path = Path.Combine(_dir, "wrong.pak");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true);
            using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
            var entry = zip.CreateEntry("manifest.txt", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write("not an OSCE dataset");
        }

        using var src = EncryptedOskeSource.Open(path);

        Assert.Empty(src.ReadForms());
        Assert.False(src.IsValid());
        Assert.Empty(src.ListAnswerKeyEcgIds("cardiology"));
    }
}
