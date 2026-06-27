using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class EcgElementGeneratorTests
{
    private static readonly EcgCalibration Cal = new(); // 500 Hz, 1024 ADC/mV
    private const int Baseline = 1024;

    [Fact]
    public void Generate_Baseline_IsFlatAtBaseline()
    {
        var s = EcgElementGenerator.Generate(EcgElement.Baseline, new EcgElementParams(100f, 0f), Cal, Baseline);
        Assert.All(s, v => Assert.Equal(Baseline, v));
    }

    [Fact]
    public void Generate_DurationMapsToSampleCount()
    {
        var s = EcgElementGenerator.Generate(EcgElement.Baseline, new EcgElementParams(1000f, 0f), Cal, Baseline);
        Assert.Equal(500, s.Length); // 1000 ms at 500 Hz
    }

    [Fact]
    public void Generate_NonPositiveDuration_ReturnsSingleSample()
    {
        var s = EcgElementGenerator.Generate(EcgElement.PWave, new EcgElementParams(0f, 0.15f), Cal, Baseline);
        Assert.Single(s);
    }

    [Fact]
    public void Generate_PWave_IsPositiveHumpReturningToBaseline()
    {
        var p = EcgElementGenerator.Defaults(EcgElement.PWave);
        var s = EcgElementGenerator.Generate(EcgElement.PWave, p, Cal, Baseline);

        Assert.Equal(45, s.Length); // 90 ms at 500 Hz
        Assert.Equal(Baseline, s[0]);
        Assert.Equal(Baseline, s[^1]);
        Assert.True(s.Max() > Baseline, "P wave should rise above the isoline");
        Assert.InRange(s.Max(), Baseline + 122, Baseline + 180); // ~0.15 mV * 1024
    }

    [Fact]
    public void Generate_Qrs_HasRSpikeAboveAndQorSBelow()
    {
        var q = EcgElementGenerator.Defaults(EcgElement.QrsComplex);
        var s = EcgElementGenerator.Generate(EcgElement.QrsComplex, q, Cal, Baseline);

        Assert.True(s.Max() > Baseline + 100, "R spike should be well above the isoline");
        Assert.True(s.Min() < Baseline, "Q/S should dip below the isoline");
    }

    [Fact]
    public void Generate_ClampsToAdcRange()
    {
        var s = EcgElementGenerator.Generate(EcgElement.QrsComplex, new EcgElementParams(90f, 100f), Cal, Baseline);
        Assert.All(s, v => Assert.InRange(v, 0, 2048));
    }
}
