using CardioSimulator.Core.Data.Wfdb;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class WfdbConverterTests
{
    private static WfdbRecord MakeTwoLeadRecord()
    {
        // gain 1000 counts/mV, baseline 0. Lead I = +1 mV flat, Lead II = -0.5 mV flat.
        var specs = new List<WfdbSignalSpec>
        {
            new() { FileName = "r.mat", Format = 16, Gain = 1000, Units = "mV", Description = "I" },
            new() { FileName = "r.mat", Format = 16, Gain = 1000, Units = "mV", Description = "II" },
        };
        var samples = new[]
        {
            Enumerable.Repeat(1000, 8).ToArray(),  // +1 mV
            Enumerable.Repeat(-500, 8).ToArray(),  // -0.5 mV
        };
        var header = new WfdbHeader
        {
            RecordName = "r",
            NumberOfSignals = 2,
            SamplingFrequency = 500,
            NumberOfSamples = 8,
            Signals = specs,
        };
        return new WfdbRecord(header, samples);
    }

    [Fact]
    public void ToPathologyFile_MapsLeadsAndRescalesToDomainUnits()
    {
        var file = WfdbConverter.ToPathologyFile(MakeTwoLeadRecord(), "rec", "Test record");

        Assert.Equal("rec", file.Id);
        Assert.Equal("Test record", file.TitleEn);
        Assert.True(file.Leads.ContainsKey(Lead.I));
        Assert.True(file.Leads.ContainsKey(Lead.II));

        // +1 mV => 1024 + 1 * 1024 = 2048; -0.5 mV => 1024 - 0.5 * 1024 = 512.
        Assert.Equal(2048, file.Leads[Lead.I].Samples[0]);
        Assert.Equal(512, file.Leads[Lead.II].Samples[0]);
    }

    [Fact]
    public void ToPathologyFile_SkipsUnrecognizedSignals()
    {
        var record = MakeTwoLeadRecord() with { };
        var specs = new List<WfdbSignalSpec>
        {
            record.Header.Signals[0] with { Description = "I" },
            record.Header.Signals[1] with { Description = "PLETH" }, // not a 12-lead lead
        };
        var withExtra = record with { Header = record.Header with { Signals = specs } };

        var file = WfdbConverter.ToPathologyFile(withExtra, "rec", "T");

        Assert.True(file.Leads.ContainsKey(Lead.I));
        Assert.Single(file.Leads);
    }

    [Fact]
    public void RoundTrip_DomainToWfdbToDomain_PreservesSamples()
    {
        var original = WfdbConverter.ToPathologyFile(MakeTwoLeadRecord(), "rec", "T");

        var wfdb = WfdbConverter.FromPathologyFile(original);
        var roundTripped = WfdbConverter.ToPathologyFile(wfdb, "rec", "T");

        foreach (var lead in original.Leads.Keys)
            Assert.Equal(original.Leads[lead].Samples, roundTripped.Leads[lead].Samples);
    }

    [Fact]
    public void FromPathologyFile_EmitsLeadsInCanonicalOrder()
    {
        var leads = new Dictionary<Lead, LeadStream>
        {
            [Lead.V1] = new(Lead.V1, new[] { 1024, 1024 }),
            [Lead.I] = new(Lead.I, new[] { 1024, 1024 }),
            [Lead.aVR] = new(Lead.aVR, new[] { 1024, 1024 }),
        };
        var file = new PathologyFile("p", "P", null, leads);

        var record = WfdbConverter.FromPathologyFile(file);

        // Canonical order: I (0), aVR (3), V1 (6) => I, aVR, V1.
        Assert.Equal(new[] { "I", "aVR", "V1" },
            record.Header.Signals.Select(s => s.Description).ToArray());
    }
}
