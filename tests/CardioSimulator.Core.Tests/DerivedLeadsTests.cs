using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class DerivedLeadsTests
{
    private static readonly float[] LeadI = { 1f, 2f, 3f };
    private static readonly float[] LeadII = { 5f, 5f, 5f };

    [Fact]
    public void III_Equals_II_Minus_I()
    {
        var iii = DerivedLeads.CombineIII_aVR_aVL_aVF(LeadI, LeadII, Lead.III);
        Assert.Equal(new[] { 4f, 3f, 2f }, iii);
    }

    [Fact]
    public void aVR_Equals_NegativeHalfSum()
    {
        var avr = DerivedLeads.CombineIII_aVR_aVL_aVF(LeadI, LeadII, Lead.aVR);
        Assert.Equal(new[] { -3f, -3.5f, -4f }, avr);
    }

    [Fact]
    public void aVL_Equals_TwoIMinusII_Over2()
    {
        var avl = DerivedLeads.CombineIII_aVR_aVL_aVF(LeadI, LeadII, Lead.aVL);
        Assert.Equal(new[] { -1.5f, -0.5f, 0.5f }, avl);
    }

    [Fact]
    public void aVF_Equals_TwoIIMinusI_Over2()
    {
        var avf = DerivedLeads.CombineIII_aVR_aVL_aVF(LeadI, LeadII, Lead.aVF);
        Assert.Equal(new[] { 4.5f, 4f, 3.5f }, avf);
    }

    [Fact]
    public void Limb_TruncatesToShorterLength()
    {
        var shortI = new[] { 1f, 2f };
        var result = DerivedLeads.CombineIII_aVR_aVL_aVF(shortI, LeadII, Lead.III);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Limb_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DerivedLeads.CombineIII_aVR_aVL_aVF(Array.Empty<float>(), LeadII, Lead.III));
    }

    [Fact]
    public void Limb_NonDerivableTarget_ReturnsEmpty()
    {
        Assert.Empty(DerivedLeads.CombineIII_aVR_aVL_aVF(LeadI, LeadII, Lead.V1));
    }

    [Fact]
    public void Precordial_IsLinearInInputs()
    {
        var v2 = new[] { 10f, -4f };
        var v6 = new[] { 3f, 7f };
        var single = DerivedLeads.CombineV1_V3_V4_V5(v2, v6, Lead.V3);

        var v2x2 = new[] { 20f, -8f };
        var v6x2 = new[] { 6f, 14f };
        var doubled = DerivedLeads.CombineV1_V3_V4_V5(v2x2, v6x2, Lead.V3);

        Assert.Equal(single.Count, doubled.Count);
        for (var i = 0; i < single.Count; i++)
        {
            Assert.Equal(2f * single[i], doubled[i], 3);
        }
    }

    [Fact]
    public void Precordial_NonDerivableTarget_ReturnsEmpty()
    {
        var v2 = new[] { 1f, 2f };
        var v6 = new[] { 3f, 4f };
        Assert.Empty(DerivedLeads.CombineV1_V3_V4_V5(v2, v6, Lead.V2));
    }

    [Fact]
    public void DerivableSets_HaveExpectedMembers()
    {
        Assert.Equal(
            new HashSet<Lead> { Lead.III, Lead.aVR, Lead.aVL, Lead.aVF },
            DerivedLeads.DerivableFromIandII.ToHashSet());
        Assert.Equal(
            new HashSet<Lead> { Lead.V1, Lead.V3, Lead.V4, Lead.V5 },
            DerivedLeads.DerivableFromV2andV6.ToHashSet());
    }
}
