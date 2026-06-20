using CardioSimulator.Core.Data.Wfdb;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class WfdbReadWriteTests
{
    // The bundled WFDB sample (an additional working directory for this repo).
    private const string SampleHeaderPath = @"E:\VLN_Project\Data\010\JS00001.hea";

    private static WfdbRecord MakeRecord(string name, int channels, int samples)
    {
        var data = new int[channels][];
        var specs = new List<WfdbSignalSpec>(channels);
        for (var c = 0; c < channels; c++)
        {
            data[c] = new int[samples];
            for (var s = 0; s < samples; s++)
                data[c][s] = (c + 1) * 100 + s - samples / 2; // varied, signed
            specs.Add(new WfdbSignalSpec
            {
                FileName = name + ".dat",
                Format = WfdbSignalCodec.Format16,
                Gain = 1000,
                Units = "mV",
                AdcResolution = 16,
                Description = $"ch{c}",
            });
        }
        var header = new WfdbHeader
        {
            RecordName = name,
            NumberOfSignals = channels,
            SamplingFrequency = 500,
            NumberOfSamples = samples,
            Signals = specs,
        };
        return new WfdbRecord(header, data);
    }

    [Theory]
    [InlineData(WfdbStorage.Dat)]
    [InlineData(WfdbStorage.Mat)]
    public void WriteThenRead_RoundTripsSamplesAndHeader(WfdbStorage storage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wfdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var record = MakeRecord("REC", channels: 4, samples: 50);
            var written = WfdbWriter.WriteRecord(record, dir, storage);

            var headerPath = Path.Combine(dir, "REC.hea");
            Assert.True(File.Exists(headerPath));
            Assert.True(File.Exists(Path.Combine(dir, "REC" + (storage == WfdbStorage.Mat ? ".mat" : ".dat"))));

            var readBack = WfdbReader.ReadRecord(headerPath);

            Assert.Equal(record.ChannelCount, readBack.ChannelCount);
            Assert.Equal(record.SampleCount, readBack.SampleCount);
            for (var c = 0; c < record.ChannelCount; c++)
                Assert.Equal(record.Samples[c], readBack.Samples[c]);

            // Writer recomputes consistent checksums and initial values.
            for (var c = 0; c < record.ChannelCount; c++)
            {
                Assert.Equal(record.Samples[c][0], written.Signals[c].InitialValue);
                Assert.Equal(WfdbWriter.ComputeChecksum(record.Samples[c]), written.Signals[c].Checksum);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteAsMat_ProducesOffset24Format16()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wfdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var header = WfdbWriter.WriteRecord(MakeRecord("MATREC", 2, 10), dir, WfdbStorage.Mat);
            Assert.All(header.Signals, s =>
            {
                Assert.EndsWith(".mat", s.FileName);
                Assert.Equal(16, s.Format);
                Assert.Equal(24, s.ByteOffset);
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadRealSample_MatchesHeaderGroundTruth()
    {
        // Ground-truth check against the bundled sample; a no-op where the data isn't present.
        if (!File.Exists(SampleHeaderPath)) return;

        var record = WfdbReader.ReadRecord(SampleHeaderPath);

        Assert.Equal(12, record.Header.NumberOfSignals);
        Assert.Equal(500, record.Header.SamplingFrequency);
        Assert.Equal(5000, record.Header.NumberOfSamples);
        Assert.Equal(5000, record.SampleCount);

        // First signal is lead I with initial value -254 (from the header).
        Assert.Equal("I", record.Header.Signals[0].Description);
        Assert.Equal(-254, record.Samples[0][0]);

        // The decoded data must reproduce every signal's declared initial value and checksum —
        // this validates both the .mat decode and the checksum algorithm against WFDB ground truth.
        for (var c = 0; c < record.Header.NumberOfSignals; c++)
        {
            var spec = record.Header.Signals[c];
            Assert.Equal(spec.InitialValue, record.Samples[c][0]);
            Assert.Equal(spec.Checksum, WfdbWriter.ComputeChecksum(record.Samples[c]));
        }
    }

    [Fact]
    public void RoundTripRealSample_PreservesSamples()
    {
        if (!File.Exists(SampleHeaderPath)) return;

        var original = WfdbReader.ReadRecord(SampleHeaderPath);
        var dir = Path.Combine(Path.GetTempPath(), "wfdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            WfdbWriter.WriteRecord(original, dir, WfdbStorage.Mat, recordName: "ROUND");
            var reread = WfdbReader.ReadRecord(Path.Combine(dir, "ROUND.hea"));

            Assert.Equal(original.ChannelCount, reread.ChannelCount);
            for (var c = 0; c < original.ChannelCount; c++)
                Assert.Equal(original.Samples[c], reread.Samples[c]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
