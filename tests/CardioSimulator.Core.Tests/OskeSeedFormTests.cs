using System.Linq;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class OskeSeedFormTests
{
    [Fact]
    public void Therapy_Has10Blocks_AllSingleSelect()
    {
        var form = OskeSeedForms.Therapy();
        Assert.Equal(OskeForms.TherapyFormId, form.FormId);
        Assert.Equal(OskeSpecialty.Therapy, form.Specialty);
        Assert.Equal(10, form.Questions.Count);
        Assert.All(form.Questions, q => Assert.Equal(OskeAnswerKind.Single, q.Kind));
    }

    [Fact]
    public void Cardiology_Has13Blocks_WithStDynamicsMultiSelect()
    {
        var form = OskeSeedForms.Cardiology(OskeSpecialty.Cardiology);
        Assert.Equal(OskeForms.CardiologyFormId, form.FormId);
        Assert.Equal(13, form.Questions.Count);

        var st = form.Questions.Single(q => q.Id == "st_dynamics");
        Assert.Equal(OskeAnswerKind.Multi, st.Kind);

        // Everything except the ST-dynamics block is single-select.
        Assert.All(form.Questions.Where(q => q.Id != "st_dynamics"),
            q => Assert.Equal(OskeAnswerKind.Single, q.Kind));
    }

    [Fact]
    public void FunctionalDiagnostics_SharesCardiologyForm()
    {
        Assert.Equal(OskeForms.CardiologyFormId, OskeForms.FormIdFor(OskeSpecialty.FunctionalDiagnostics));
        var form = OskeSeedForms.For(OskeSpecialty.FunctionalDiagnostics);
        Assert.Equal(OskeForms.CardiologyFormId, form.FormId);
        Assert.Equal(13, form.Questions.Count);
        Assert.Equal(OskeSpecialty.FunctionalDiagnostics, form.Specialty);
    }

    [Fact]
    public void AllForms_HaveUniqueQuestionIds_AndNumberedSequentially()
    {
        foreach (var form in OskeSeedForms.All())
        {
            var ids = form.Questions.Select(q => q.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(Enumerable.Range(1, form.Questions.Count), form.Questions.Select(q => q.Number));
        }
    }

    [Fact]
    public void AllQuestions_HaveNonEmptyTitleAndOptions_WithUniqueOptionIds()
    {
        foreach (var form in OskeSeedForms.All())
        {
            Assert.All(form.Questions, q =>
            {
                Assert.False(string.IsNullOrWhiteSpace(q.Title));
                Assert.NotEmpty(q.Options);
                Assert.All(q.Options, o =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(o.Id));
                    Assert.False(string.IsNullOrWhiteSpace(o.Text));
                });
                var optionIds = q.Options.Select(o => o.Id).ToList();
                Assert.Equal(optionIds.Count, optionIds.Distinct().Count());
            });
        }
    }
}
