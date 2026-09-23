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
/// The bundled question bank: a pack read in memory (<see cref="EncryptedQuestionBankSource"/>) with
/// the instructor's own questions layered over it (<see cref="CompositeQuestionBankSource"/>).
/// </summary>
public class BundledQuestionBankTests : IDisposable
{
    private readonly string _dir;

    public BundledQuestionBankTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bank_pak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static TestQuestion Question(string id, string text, string theme = "Основы ЭКГ") => new(
        id, 0, text,
        new List<TestOption> { new("a", "A"), new("b", "B") },
        "a", "Because A.", Theme: theme);

    /// <summary>Builds a bank pack the way tools/QuestionBankPacker does.</summary>
    private string WritePack(params TestQuestion[] questions)
    {
        var path = Path.Combine(_dir, "QuestionBank.pak");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true);
            using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
            var entry = zip.CreateEntry(EncryptedQuestionBankSource.BankEntryPath, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(TestJson.SerializeBank(questions));
        }
        return path;
    }

    [Fact]
    public void PackedBankRoundTrips()
    {
        var path = WritePack(Question("q1", "First?"), Question("q2", "Second?"));

        using var src = EncryptedQuestionBankSource.Open(path);

        Assert.True(src.IsValid());
        Assert.Equal(2, src.ReadQuestions().Count);
        Assert.Equal("First?", src.ReadQuestion("q1")!.Text);
        Assert.Null(src.ReadQuestion("nope"));
    }

    /// <summary>The fresh-install path — no authored questions yet, which is what every user hits
    /// first. It must return the file source's theme-then-text order, not the pack's raw order, or the
    /// whole list reshuffles the moment they save their first question.</summary>
    [Fact]
    public void BundledOnlyBankIsSortedLikeTheDiskBank()
    {
        var path = WritePack(
            Question("q3", "Zeta question?", theme: "Zeta theme"),
            Question("q1", "Alpha question?", theme: "Alpha theme"),
            Question("q2", "Mu question?", theme: "Mu theme"));
        var ownDir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(ownDir);

        using var bundled = EncryptedQuestionBankSource.Open(path);
        var composite = new CompositeQuestionBankSource(bundled, new FileQuestionBankSource(ownDir));

        Assert.Equal(
            new[] { "Alpha theme", "Mu theme", "Zeta theme" },
            composite.ReadQuestions().Select(q => q.Theme).ToArray());
    }

    [Fact]
    public void PackIsNotPlaintext()
    {
        var path = WritePack(Question("q1", "A distinctive question body"));

        var bytes = File.ReadAllBytes(path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("A distinctive question body", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeUnionsBundledAndOwnQuestions()
    {
        var packPath = WritePack(Question("bundled1", "Shipped one?"), Question("bundled2", "Shipped two?"));
        var ownDir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(ownDir);
        var own = new FileQuestionBankSource(ownDir);
        own.WriteQuestion(Question("mine", "Authored here?"));

        using var bundled = EncryptedQuestionBankSource.Open(packPath);
        var composite = new CompositeQuestionBankSource(bundled, own);

        var ids = composite.ReadQuestions().Select(q => q.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "bundled1", "bundled2", "mine" }, ids);
    }

    [Fact]
    public void OwnQuestionShadowsBundledOneWithTheSameId()
    {
        var packPath = WritePack(Question("shared", "Shipped wording?"));
        var ownDir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(ownDir);
        var own = new FileQuestionBankSource(ownDir);
        own.WriteQuestion(Question("shared", "Corrected wording?"));

        using var bundled = EncryptedQuestionBankSource.Open(packPath);
        var composite = new CompositeQuestionBankSource(bundled, own);

        var all = composite.ReadQuestions();
        Assert.Single(all);
        Assert.Equal("Corrected wording?", all[0].Text);
        Assert.Equal("Corrected wording?", composite.ReadQuestion("shared")!.Text);
    }

    [Fact]
    public void RepositoryStillWritesThroughACompositeSource()
    {
        var packPath = WritePack(Question("bundled1", "Shipped?"));
        var ownDir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(ownDir);

        using var bundled = EncryptedQuestionBankSource.Open(packPath);
        var repo = new QuestionBankRepository(
            new CompositeQuestionBankSource(bundled, new FileQuestionBankSource(ownDir)));

        Assert.True(repo.WriteQuestion(Question("mine", "Authored?")));
        Assert.Equal(2, repo.Questions.Count);

        // Deleting an authored question removes it. Deleting a bundled one reports success (the
        // file-level delete is idempotent) but the pack is read-only, so the question is still there.
        Assert.True(repo.DeleteQuestion("mine"));
        Assert.Single(repo.Questions);
        repo.DeleteQuestion("bundled1");
        Assert.Equal("bundled1", Assert.Single(repo.Questions).Id);
    }

    [Fact]
    public void ImportGoesToDiskNotThePack()
    {
        var packPath = WritePack(Question("bundled1", "Shipped?"));
        var ownDir = Path.Combine(_dir, "own");
        Directory.CreateDirectory(ownDir);

        using var bundled = EncryptedQuestionBankSource.Open(packPath);
        var repo = new QuestionBankRepository(
            new CompositeQuestionBankSource(bundled, new FileQuestionBankSource(ownDir)));

        Assert.Equal(2, repo.Import(new[] { Question("i1", "One?"), Question("i2", "Two?") }));
        Assert.Equal(3, repo.Questions.Count);
        Assert.Equal(2, Directory.GetFiles(ownDir, "*.json").Length);
        Assert.Equal(File.ReadAllBytes(packPath).Length, new FileInfo(packPath).Length);
    }

    /// <summary>A pack without the bank entry (e.g. a course pack pointed at the wrong slot) reads as
    /// an empty bank instead of taking the app down; AppViewModel then falls back to the disk bank.</summary>
    [Fact]
    public void PackWithoutABankEntryReadsAsEmpty()
    {
        var path = Path.Combine(_dir, "wrong.pak");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true);
            using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
            var entry = zip.CreateEntry("manifest.txt", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write("not a question bank");
        }

        using var src = EncryptedQuestionBankSource.Open(path);

        Assert.Empty(src.ReadQuestions());
        Assert.False(src.IsValid());
    }

    /// <summary>Bytes that are not a pack at all throw on open (as documented) rather than reading as
    /// empty — AppViewModel catches that and falls back to the disk bank.</summary>
    [Fact]
    public void GarbagePackThrowsOnOpen()
    {
        var path = Path.Combine(_dir, "broken.pak");
        File.WriteAllBytes(path, ContentCrypto.Encrypt(new byte[] { 1, 2, 3, 4, 5 }));

        Assert.ThrowsAny<Exception>(() => EncryptedQuestionBankSource.Open(path));
    }
}
