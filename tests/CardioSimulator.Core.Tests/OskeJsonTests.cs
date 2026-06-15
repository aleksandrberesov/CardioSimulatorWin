using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class OskeJsonTests
{
    [Fact]
    public void Form_RoundTrips_WithKindsTitlesAndOptions()
    {
        var original = OskeSeedForms.Cardiology(OskeSpecialty.Cardiology);

        var json = OskeJson.SerializeForm(original);
        var round = OskeJson.DeserializeForm(json);

        Assert.NotNull(round);
        Assert.Equal(original.FormId, round!.FormId);
        Assert.Equal(original.Specialty, round.Specialty);
        Assert.Equal(original.Version, round.Version);
        Assert.Equal(original.PassFraction, round.PassFraction);
        Assert.Equal(original.Questions.Count, round.Questions.Count);

        for (var i = 0; i < original.Questions.Count; i++)
        {
            var a = original.Questions[i];
            var b = round.Questions[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Number, b.Number);
            Assert.Equal(a.Title, b.Title);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Options.Select(o => (o.Id, o.Text)), b.Options.Select(o => (o.Id, o.Text)));
        }

        // The multi-select block must survive serialization.
        Assert.Equal(OskeAnswerKind.Multi, round.Questions.Single(q => q.Id == "st_dynamics").Kind);
    }

    [Fact]
    public void Form_Json_WritesCyrillicLiterally_AndEnumsAsStrings()
    {
        var json = OskeJson.SerializeForm(OskeSeedForms.Therapy());
        Assert.Contains("Синусовый", json);          // not Си...
        Assert.Contains("\"single\"", json);          // OskeAnswerKind.Single, camelCased property + string enum
        Assert.DoesNotContain("\\u04", json);          // no escaped Cyrillic
    }

    [Fact]
    public void AnswerKey_RoundTrips()
    {
        var key = new OskeAnswerKey("afib_01", OskeForms.CardiologyFormId,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["rhythm"] = new[] { "afib" },
                ["st_dynamics"] = new[] { "depression", "arrhythmia_obscures" },
            });

        var round = OskeJson.DeserializeAnswerKey(OskeJson.SerializeAnswerKey(key));

        Assert.NotNull(round);
        Assert.Equal("afib_01", round!.EcgId);
        Assert.Equal(OskeForms.CardiologyFormId, round.FormId);
        Assert.Equal(new[] { "afib" }, round.CorrectOptionIds["rhythm"]);
        Assert.Equal(new[] { "depression", "arrhythmia_obscures" }, round.CorrectOptionIds["st_dynamics"]);
    }

    [Fact]
    public void Result_RoundTrips()
    {
        var form = OskeSeedForms.Therapy();
        var key = new OskeAnswerKey("ecg1", form.FormId,
            form.Questions.ToDictionary(q => q.Id, q => (IReadOnlyList<string>)new[] { q.Options[0].Id }));
        var selections = form.Questions.ToDictionary(q => q.Id, q => (IReadOnlyList<string>)new[] { q.Options[0].Id });

        var result = OskeGrader.Grade(form, key, selections,
            new OskeStudentInfo("Петров П.П.", "К-205"), "ecg1",
            new DateTimeOffset(2026, 6, 14, 10, 22, 5, TimeSpan.Zero));

        var round = OskeJson.DeserializeResult(OskeJson.SerializeResult(result));

        Assert.NotNull(round);
        Assert.Equal("Петров П.П.", round!.Student.FullName);
        Assert.Equal("К-205", round.Student.Group);
        Assert.Equal(OskeSpecialty.Therapy, round.Specialty);
        Assert.Equal(result.Timestamp, round.Timestamp);
        Assert.Equal(result.CorrectCount, round.CorrectCount);
        Assert.Equal(result.TotalCount, round.TotalCount);
        Assert.Equal(result.Passed, round.Passed);
        Assert.Equal(result.Blocks.Count, round.Blocks.Count);
    }
}
