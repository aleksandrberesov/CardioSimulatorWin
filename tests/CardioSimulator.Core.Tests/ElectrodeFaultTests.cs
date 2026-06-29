using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class ElectrodeFaultTests
{
    // Baseline-zeroed sample stand-ins; the actual values only need to be distinguishable per lead.
    private static Points P(params float[] v) => new(v);

    private static Dictionary<Lead, Points> TwelveLead() => new()
    {
        [Lead.I] = P(1f, 2f, -3f),
        [Lead.II] = P(10f, 20f, 30f),
        [Lead.III] = P(100f, 200f, 300f),
        [Lead.aVR] = P(-5f, -6f, -7f),
        [Lead.aVL] = P(40f, 41f, 42f),
        [Lead.aVF] = P(7f, 8f, 9f),
        [Lead.V1] = P(2f, 4f, 6f),
        [Lead.V2] = P(3f, 6f, 9f),
        [Lead.V3] = P(0f, 0f, 0f),
        [Lead.V4] = P(1f, 1f, 1f),
        [Lead.V5] = P(8f, 8f, 8f),
        [Lead.V6] = P(5f, 5f, 5f),
    };

    [Fact]
    public void Ok_ReturnsSameInstanceUnchanged()
    {
        var input = TwelveLead();
        var result = ElectrodeFault.Apply(input, ElectrodeState.Ok);
        Assert.Same(input, result);
    }

    [Fact]
    public void Swapped_InvertsI_SwapsIIandIII_SwapsAvrAndAvl()
    {
        var input = TwelveLead();
        var result = ElectrodeFault.Apply(input, ElectrodeState.Swapped);

        // I inverts.
        Assert.Equal(new[] { -1f, -2f, 3f }, result[Lead.I].Values);
        // II <-> III exchange.
        Assert.Equal(input[Lead.III].Values, result[Lead.II].Values);
        Assert.Equal(input[Lead.II].Values, result[Lead.III].Values);
        // aVR <-> aVL exchange.
        Assert.Equal(input[Lead.aVL].Values, result[Lead.aVR].Values);
        Assert.Equal(input[Lead.aVR].Values, result[Lead.aVL].Values);
    }

    [Fact]
    public void Swapped_LeavesAvfAndPrecordialUntouched()
    {
        var input = TwelveLead();
        var result = ElectrodeFault.Apply(input, ElectrodeState.Swapped);

        Assert.Equal(input[Lead.aVF].Values, result[Lead.aVF].Values);
        foreach (var v in new[] { Lead.V1, Lead.V2, Lead.V3, Lead.V4, Lead.V5, Lead.V6 })
            Assert.Equal(input[v].Values, result[v].Values);
    }

    [Fact]
    public void Swapped_DoesNotMutateInput()
    {
        var input = TwelveLead();
        ElectrodeFault.Apply(input, ElectrodeState.Swapped);

        Assert.Equal(new[] { 1f, 2f, -3f }, input[Lead.I].Values);
        Assert.Equal(new[] { 10f, 20f, 30f }, input[Lead.II].Values);
    }

    [Fact]
    public void Displacement_AttenuatesPrecordial_LeavesLimbLeadsUntouched()
    {
        var input = TwelveLead();
        var result = ElectrodeFault.Apply(input, ElectrodeState.Displacement);

        // Limb leads unchanged.
        foreach (var limb in new[] { Lead.I, Lead.II, Lead.III, Lead.aVR, Lead.aVL, Lead.aVF })
            Assert.Equal(input[limb].Values, result[limb].Values);

        // Precordial leads attenuated below their original magnitude (and not zeroed).
        Assert.Equal(2f * 0.55f, result[Lead.V1].Values[0], 3);
        Assert.Equal(9f * 0.55f, result[Lead.V2].Values[2], 3);
        Assert.Equal(8f * 0.55f, result[Lead.V5].Values[0], 3);
    }

    [Fact]
    public void Swapped_SixLimbOnly_DoesNotThrow_AndSkipsMissingLeads()
    {
        var input = new Dictionary<Lead, Points>
        {
            [Lead.I] = P(1f, 2f),
            [Lead.II] = P(3f, 4f),
            // No III / aVL / precordial leads (e.g. an EMD recording).
        };

        var result = ElectrodeFault.Apply(input, ElectrodeState.Swapped);

        Assert.Equal(new[] { -1f, -2f }, result[Lead.I].Values);
        // II had no III to swap with, so the original II moves to III and II itself is left as-is.
        Assert.Equal(new[] { 3f, 4f }, result[Lead.III].Values);
        Assert.False(result.ContainsKey(Lead.aVL));
    }
}
