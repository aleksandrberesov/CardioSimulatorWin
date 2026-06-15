using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class FileOskeSourceTests : IDisposable
{
    private readonly string _dir;

    public FileOskeSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oske_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void IsValid_FalseUntilAFormIsWritten()
    {
        var source = new FileOskeSource(_dir);
        Assert.False(source.IsValid());

        Assert.True(source.WriteForm(OskeSeedForms.Therapy()));
        Assert.True(source.IsValid());
    }

    [Fact]
    public void WriteForm_ThenRead_RoundTrips()
    {
        var source = new FileOskeSource(_dir);
        var original = OskeSeedForms.Cardiology(OskeSpecialty.Cardiology);

        Assert.True(source.WriteForm(original));

        var read = source.ReadForm(OskeForms.CardiologyFormId);
        Assert.NotNull(read);
        Assert.Equal(13, read!.Questions.Count);
        Assert.Equal(OskeAnswerKind.Multi, read.Questions.Single(q => q.Id == "st_dynamics").Kind);
    }

    [Fact]
    public void ReadForms_ReturnsAllSeededForms()
    {
        var source = new FileOskeSource(_dir);
        foreach (var form in OskeSeedForms.All()) source.WriteForm(form);

        var forms = source.ReadForms();
        Assert.Equal(2, forms.Count);
        Assert.Contains(forms, f => f.FormId == OskeForms.TherapyFormId);
        Assert.Contains(forms, f => f.FormId == OskeForms.CardiologyFormId);
    }

    [Fact]
    public void WriteAnswerKey_ThenRead_RoundTrips()
    {
        var source = new FileOskeSource(_dir);
        var key = new OskeAnswerKey("afib_01", OskeForms.TherapyFormId,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["rhythm"] = new[] { "afib" },
                ["heart_rate"] = new[] { "from_50_to_101" },
            });

        Assert.True(source.WriteAnswerKey(key));

        var read = source.ReadAnswerKey("afib_01", OskeForms.TherapyFormId);
        Assert.NotNull(read);
        Assert.Equal(new[] { "afib" }, read!.CorrectOptionIds["rhythm"]);
        Assert.Null(source.ReadAnswerKey("afib_01", OskeForms.CardiologyFormId)); // different form, no key
    }

    [Fact]
    public void ListAnswerKeyEcgIds_ListsOnlyEcgsWithAKeyForThatForm()
    {
        var source = new FileOskeSource(_dir);
        source.WriteAnswerKey(new OskeAnswerKey("ecg_a", OskeForms.TherapyFormId,
            new Dictionary<string, IReadOnlyList<string>> { ["rhythm"] = new[] { "sinus" } }));
        source.WriteAnswerKey(new OskeAnswerKey("ecg_b", OskeForms.CardiologyFormId,
            new Dictionary<string, IReadOnlyList<string>> { ["rhythm"] = new[] { "sinus" } }));

        Assert.Equal(new[] { "ecg_a" }, source.ListAnswerKeyEcgIds(OskeForms.TherapyFormId));
        Assert.Equal(new[] { "ecg_b" }, source.ListAnswerKeyEcgIds(OskeForms.CardiologyFormId));
    }

    [Fact]
    public void DeleteAnswerKey_RemovesKey_AndEmptiedFolder()
    {
        var source = new FileOskeSource(_dir);
        source.WriteAnswerKey(new OskeAnswerKey("ecg_a", OskeForms.TherapyFormId,
            new Dictionary<string, IReadOnlyList<string>> { ["rhythm"] = new[] { "sinus" } }));

        Assert.True(source.DeleteAnswerKey("ecg_a", OskeForms.TherapyFormId));
        Assert.Null(source.ReadAnswerKey("ecg_a", OskeForms.TherapyFormId));
        Assert.False(Directory.Exists(Path.Combine(_dir, "answers", "ecg_a")));
    }

    [Fact]
    public void UnsafeIds_AreRejected()
    {
        var source = new FileOskeSource(_dir);
        Assert.Null(source.ReadAnswerKey("..", OskeForms.TherapyFormId));
        Assert.False(source.WriteAnswerKey(new OskeAnswerKey("../evil", OskeForms.TherapyFormId,
            new Dictionary<string, IReadOnlyList<string>>())));
    }
}
