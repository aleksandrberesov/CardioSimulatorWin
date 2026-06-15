using System;
using System.Collections.Generic;
using System.IO;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class OskeRepositoryTests : IDisposable
{
    private readonly string _dir;

    public OskeRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oske_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FormFor_FallsBackToSeed_WhenNothingOnDisk()
    {
        var repo = new OskeRepository(new FileOskeSource(_dir));
        Assert.Empty(repo.Forms);

        var form = repo.FormFor(OskeSpecialty.Cardiology);
        Assert.Equal(OskeForms.CardiologyFormId, form.FormId);
        Assert.Equal(13, form.Questions.Count);
    }

    [Fact]
    public void WriteForm_InvalidatesCache_AndRaisesChanged()
    {
        var repo = new OskeRepository(new FileOskeSource(_dir));
        var changed = 0;
        repo.Changed += (_, _) => changed++;

        Assert.True(repo.WriteForm(OskeSeedForms.Therapy()));
        Assert.True(changed >= 1);
        Assert.Single(repo.Forms);
        Assert.Equal(OskeForms.TherapyFormId, repo.Form(OskeForms.TherapyFormId)!.FormId);
    }

    [Fact]
    public void WriteAnswerKey_ThenQuery_Works_AndRaisesChanged()
    {
        var repo = new OskeRepository(new FileOskeSource(_dir));
        repo.WriteForm(OskeSeedForms.Therapy());

        var changed = 0;
        repo.Changed += (_, _) => changed++;

        var key = new OskeAnswerKey("ecg_a", OskeForms.TherapyFormId,
            new Dictionary<string, IReadOnlyList<string>> { ["rhythm"] = new[] { "sinus" } });
        Assert.True(repo.WriteAnswerKey(key));
        Assert.True(changed >= 1);

        Assert.True(repo.HasAnswerKey("ecg_a", OskeSpecialty.Therapy));
        Assert.False(repo.HasAnswerKey("ecg_a", OskeSpecialty.Cardiology));
        Assert.Equal(new[] { "ecg_a" }, repo.AnswerKeyEcgIds(OskeSpecialty.Therapy));
        Assert.Equal(new[] { "sinus" }, repo.AnswerKey("ecg_a", OskeSpecialty.Therapy)!.CorrectOptionIds["rhythm"]);
    }
}
