using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class QuestionBankTests : IDisposable
{
    private readonly string _dir;

    public QuestionBankTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bank_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static TestQuestion ImageQuestion(string id = "q_img") => new(
        id, 0, "What does this picture show?",
        new List<TestOption> { new("a", "A"), new("b", "B"), new("c", "C"), new("d", "D") },
        "b", "Because B.",
        ImagePath: id + ".png", Theme: "Инфаркт миокарда", Tags: new[] { "острый", "передний" });

    private static TestQuestion EcgQuestion(string id = "q_ecg") => new(
        id, 0, "Name the rhythm.",
        new List<TestOption> { new("a", "Sinus"), new("b", "AF") },
        "a", "Sinus.", PathologyId: "sinus_norm", Theme: "Нарушения ритма");

    // ── Domain ────────────────────────────────────────────────────────────────

    [Fact]
    public void Stimulus_IsDerived_FromContent()
    {
        Assert.Equal(QuestionStimulus.Image, ImageQuestion().Stimulus);
        Assert.Equal(QuestionStimulus.Ecg, EcgQuestion().Stimulus);
        Assert.Equal(QuestionStimulus.Text, new TestQuestion(
            "t", 0, "Text only?", new List<TestOption> { new("a", "A"), new("b", "B") }, "a", "").Stimulus);
    }

    [Fact]
    public void TagList_NeverNull()
    {
        Assert.Empty(new TestQuestion("t", 0, "Q", Array.Empty<TestOption>(), "", "").TagList);
        Assert.Equal(new[] { "острый", "передний" }, ImageQuestion().TagList);
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Question_Json_RoundTrips_AllFields()
    {
        var q = ImageQuestion();
        var read = TestJson.DeserializeQuestion(TestJson.SerializeQuestion(q));
        Assert.NotNull(read);
        Assert.Equal(q.Id, read!.Id);
        Assert.Equal(q.ImagePath, read.ImagePath);
        Assert.Equal(q.Theme, read.Theme);
        Assert.Equal(q.TagList, read.TagList);
        Assert.Equal(QuestionStimulus.Image, read.Stimulus);
    }

    [Fact]
    public void Bank_Json_RoundTrips_List()
    {
        var list = new[] { ImageQuestion("a"), EcgQuestion("b") };
        var read = TestJson.DeserializeBank(TestJson.SerializeBank(list));
        Assert.Equal(2, read.Count);
        Assert.Equal(new[] { "a", "b" }, read.Select(q => q.Id));
    }

    // ── FileQuestionBankSource ──────────────────────────────────────────────────

    [Fact]
    public void Source_Write_Read_RoundTrips()
    {
        var src = new FileQuestionBankSource(_dir);
        Assert.True(src.WriteQuestion(ImageQuestion()));

        var read = src.ReadQuestion("q_img");
        Assert.NotNull(read);
        Assert.Equal("Инфаркт миокарда", read!.Theme);
    }

    [Fact]
    public void Source_ReadQuestions_SkipsThemesFileAndBadFiles()
    {
        var src = new FileQuestionBankSource(_dir);
        src.WriteQuestion(ImageQuestion("a"));
        src.WriteQuestion(EcgQuestion("b"));
        File.WriteAllText(Path.Combine(_dir, FileQuestionBankSource.ThemesFileName), "[\"Тема\"]");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");

        var all = src.ReadQuestions();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, q => q.Text.Contains("Тема"));
    }

    [Fact]
    public void Source_UnsafeIds_AreRejected()
    {
        var src = new FileQuestionBankSource(_dir);
        Assert.False(src.WriteQuestion(ImageQuestion("../escape")));
        Assert.Null(src.ReadQuestion(".."));
        Assert.False(src.DeleteQuestion("../escape"));
    }

    // ── QuestionBankRepository ──────────────────────────────────────────────────

    [Fact]
    public void Repository_Caches_AndRaisesChanged()
    {
        var repo = new QuestionBankRepository(new FileQuestionBankSource(_dir));
        Assert.Empty(repo.Questions);

        var changed = 0;
        repo.Changed += (_, _) => changed++;

        Assert.True(repo.WriteQuestion(ImageQuestion()));
        Assert.True(changed >= 1);
        Assert.Single(repo.Questions);
        Assert.True(repo.DeleteQuestion("q_img"));
        Assert.Empty(repo.Questions);
    }

    [Fact]
    public void Repository_Import_WritesBatch_AndExportRoundTrips()
    {
        var repo = new QuestionBankRepository(new FileQuestionBankSource(_dir));
        var imported = repo.Import(new[] { ImageQuestion("a"), EcgQuestion("b") });
        Assert.Equal(2, imported);
        Assert.Equal(2, repo.Questions.Count);

        var exported = TestJson.DeserializeBank(repo.ExportAll());
        Assert.Equal(2, exported.Count);
        Assert.Contains(exported, q => q.Id == "a");
        Assert.Contains(exported, q => q.Id == "b");
    }

    [Fact]
    public void Repository_UsedThemes_AreDistinctAndSorted()
    {
        var repo = new QuestionBankRepository(new FileQuestionBankSource(_dir));
        repo.Import(new[] { ImageQuestion("a"), ImageQuestion("a2"), EcgQuestion("b") });
        var themes = repo.UsedThemes();
        Assert.Equal(new[] { "Инфаркт миокарда", "Нарушения ритма" }, themes);
    }

    // ── TestThemeStore ──────────────────────────────────────────────────────────

    [Fact]
    public void Themes_SeedIfMissing_ThenAddRemove()
    {
        var file = Path.Combine(_dir, "themes.json");
        var store = new TestThemeStore(file);

        Assert.Empty(store.Read());
        store.SeedIfMissing();
        Assert.Equal(TestThemeStore.DefaultThemes, store.Read());

        // SeedIfMissing is a no-op once the file exists.
        store.Write(new[] { "Only" });
        store.SeedIfMissing();
        Assert.Equal(new[] { "Only" }, store.Read());

        Assert.True(store.Add("Новая"));
        Assert.False(store.Add("новая")); // case-insensitive duplicate
        Assert.Contains("Новая", store.Read());

        Assert.True(store.Remove("only"));
        Assert.DoesNotContain("Only", store.Read());
    }

    [Fact]
    public void Themes_Write_Dedupes_PreservingFirstOrder()
    {
        var store = new TestThemeStore(Path.Combine(_dir, "themes.json"));
        store.Write(new[] { "A", "B", "a", " A ", "C" });
        Assert.Equal(new[] { "A", "B", "C" }, store.Read());
    }
}
