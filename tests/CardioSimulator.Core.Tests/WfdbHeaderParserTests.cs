using CardioSimulator.Core.Data.Wfdb;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class WfdbHeaderParserTests
{
    // The real header of the bundled 010/JS00001 sample.
    private const string Js00001Header =
        "JS00001 12 500 5000\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -254 21756 0 I\n" +
        "JS00001.mat 16+24 1000/mV 16 0 264 -599 0 II\n" +
        "JS00001.mat 16+24 1000/mV 16 0 517 -22376 0 III\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -5 28232 0 aVR\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -386 16619 0 aVL\n" +
        "JS00001.mat 16+24 1000/mV 16 0 390 15121 0 aVF\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -98 1568 0 V1\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -312 -32761 0 V2\n" +
        "JS00001.mat 16+24 1000/mV 16 0 -98 32715 0 V3\n" +
        "JS00001.mat 16+24 1000/mV 16 0 810 15193 0 V4\n" +
        "JS00001.mat 16+24 1000/mV 16 0 810 14081 0 V5\n" +
        "JS00001.mat 16+24 1000/mV 16 0 527 32579 0 V6\n" +
        "#Age: 85\n" +
        "#Sex: Male\n" +
        "#Dx: 164889003,59118001,164934002\n";

    [Fact]
    public void Parse_RecordLine_ReadsCountFrequencyAndLength()
    {
        var h = WfdbHeaderParser.Parse(Js00001Header);

        Assert.Equal("JS00001", h.RecordName);
        Assert.Equal(12, h.NumberOfSignals);
        Assert.Equal(500, h.SamplingFrequency);
        Assert.Equal(5000, h.NumberOfSamples);
        Assert.Equal(12, h.Signals.Count);
    }

    [Fact]
    public void Parse_SignalLine_ReadsFormatOffsetGainAndDescription()
    {
        var s = WfdbHeaderParser.Parse(Js00001Header).Signals[0];

        Assert.Equal("JS00001.mat", s.FileName);
        Assert.Equal(16, s.Format);
        Assert.Equal(24, s.ByteOffset);
        Assert.Equal(1000, s.Gain);
        Assert.Equal("mV", s.Units);
        Assert.Equal(16, s.AdcResolution);
        Assert.Equal(0, s.AdcZero);
        Assert.Equal(-254, s.InitialValue);
        Assert.Equal(21756, s.Checksum);
        Assert.Equal("I", s.Description);
    }

    [Fact]
    public void Parse_KeepsCommentsVerbatim()
    {
        var h = WfdbHeaderParser.Parse(Js00001Header);

        Assert.Equal(3, h.Comments.Count);
        Assert.Equal("Age: 85", h.Comments[0]);
        Assert.Equal("Dx: 164889003,59118001,164934002", h.Comments[2]);
    }

    [Fact]
    public void Parse_GainWithExplicitBaseline_IsCaptured()
    {
        const string text =
            "rec 1 250 10\n" +
            "rec.dat 16 200(50)/mV 12 1024 0 0 0 II\n";

        var s = WfdbHeaderParser.Parse(text).Signals[0];

        Assert.Equal(200, s.Gain);
        Assert.True(s.BaselineSpecified);
        Assert.Equal(50, s.Baseline);
        Assert.Equal(50, s.EffectiveBaseline);
    }

    [Fact]
    public void Parse_GainZero_FallsBackToDefaultGain()
    {
        const string text =
            "rec 1 250 10\n" +
            "rec.dat 16 0 12 0 0 0 0 II\n";

        var s = WfdbHeaderParser.Parse(text).Signals[0];

        Assert.Equal(0, s.Gain);
        Assert.Equal(WfdbConstants.DefaultGain, s.EffectiveGain);
    }

    [Fact]
    public void Parse_BaselineDefaultsToAdcZero_WhenUnspecified()
    {
        const string text =
            "rec 1 250 10\n" +
            "rec.dat 16 200/mV 12 1024 0 0 0 II\n";

        var s = WfdbHeaderParser.Parse(text).Signals[0];

        Assert.False(s.BaselineSpecified);
        Assert.Equal(1024, s.AdcZero);
        Assert.Equal(1024, s.EffectiveBaseline);
    }

    [Fact]
    public void Serialize_RoundTrips_RealHeader()
    {
        var parsed = WfdbHeaderParser.Parse(Js00001Header);
        var serialized = WfdbHeaderParser.Serialize(parsed);
        var reparsed = WfdbHeaderParser.Parse(serialized);

        Assert.Equal(parsed.RecordName, reparsed.RecordName);
        Assert.Equal(parsed.NumberOfSignals, reparsed.NumberOfSignals);
        Assert.Equal(parsed.SamplingFrequency, reparsed.SamplingFrequency);
        Assert.Equal(parsed.NumberOfSamples, reparsed.NumberOfSamples);
        Assert.Equal(parsed.Signals.Count, reparsed.Signals.Count);
        for (var i = 0; i < parsed.Signals.Count; i++)
        {
            Assert.Equal(parsed.Signals[i].Description, reparsed.Signals[i].Description);
            Assert.Equal(parsed.Signals[i].InitialValue, reparsed.Signals[i].InitialValue);
            Assert.Equal(parsed.Signals[i].Checksum, reparsed.Signals[i].Checksum);
            Assert.Equal(parsed.Signals[i].ByteOffset, reparsed.Signals[i].ByteOffset);
        }
        Assert.Equal(parsed.Comments, reparsed.Comments);
    }

    [Fact]
    public void Parse_FormatModifiers_AreReadBack()
    {
        const string text =
            "rec 1 250 10\n" +
            "rec.dat 16x2:3+44 200/mV 12 0 0 0 0 II\n";

        var s = WfdbHeaderParser.Parse(text).Signals[0];

        Assert.Equal(16, s.Format);
        Assert.Equal(2, s.SamplesPerFrame);
        Assert.Equal(3, s.Skew);
        Assert.Equal(44, s.ByteOffset);
    }
}
