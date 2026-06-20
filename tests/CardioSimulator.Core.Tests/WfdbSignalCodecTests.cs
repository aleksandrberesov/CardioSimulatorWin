using CardioSimulator.Core.Data.Wfdb;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class WfdbSignalCodecTests
{
    [Fact]
    public void Format16_EncodeDecode_RoundTrips()
    {
        var samples = new[]
        {
            new[] { -254, 100, -1, 32000 },
            new[] { 264, -200, 0, -32000 },
        };

        var bytes = WfdbSignalCodec.Encode(WfdbSignalCodec.Format16, samples);
        var decoded = WfdbSignalCodec.Decode(WfdbSignalCodec.Format16, bytes, 0, 2, 4);

        Assert.Equal(samples[0], decoded[0]);
        Assert.Equal(samples[1], decoded[1]);
    }

    [Fact]
    public void Format16_InterleavesChannelsByFrame()
    {
        var samples = new[]
        {
            new[] { 1, 2 }, // channel 0
            new[] { 3, 4 }, // channel 1
        };

        var bytes = WfdbSignalCodec.Encode(WfdbSignalCodec.Format16, samples);

        // Frame-interleaved: sample0(ch0, ch1), sample1(ch0, ch1) => 1, 3, 2, 4
        Assert.Equal(new byte[] { 1, 0, 3, 0, 2, 0, 4, 0 }, bytes);
    }

    [Fact]
    public void Format80_DecodesOffsetBinary()
    {
        var data = new byte[] { 0x00, 0x80, 0xFF };

        var flat = WfdbSignalCodec.DecodeFlat(WfdbSignalCodec.Format80, data, 3);

        Assert.Equal(new[] { -128, 0, 127 }, flat);
    }

    [Fact]
    public void Format212_DecodesPackedTwelveBitPair()
    {
        // A = 5 (0x005), B = -3 (0xFFD) packed into 3 bytes.
        var data = new byte[] { 0x05, 0xF0, 0xFD };

        var flat = WfdbSignalCodec.DecodeFlat(WfdbSignalCodec.Format212, data, 2);

        Assert.Equal(new[] { 5, -3 }, flat);
    }

    [Fact]
    public void Format212_HandlesOddSampleCount()
    {
        // Single sample A = 5 uses only the first two bytes.
        var data = new byte[] { 0x05, 0x00 };

        var flat = WfdbSignalCodec.DecodeFlat(WfdbSignalCodec.Format212, data, 1);

        Assert.Equal(new[] { 5 }, flat);
    }

    [Fact]
    public void Format61_DecodesBigEndian()
    {
        var data = new byte[] { 0x01, 0x00 }; // big-endian 0x0100 = 256

        var flat = WfdbSignalCodec.DecodeFlat(WfdbSignalCodec.Format61, data, 1);

        Assert.Equal(new[] { 256 }, flat);
    }

    [Fact]
    public void ReshapeAndFlatten_AreInverses()
    {
        var samples = new[]
        {
            new[] { 1, 2, 3 },
            new[] { 4, 5, 6 },
        };

        var flat = WfdbSignalCodec.Flatten(samples);
        Assert.Equal(new[] { 1, 4, 2, 5, 3, 6 }, flat);

        var round = WfdbSignalCodec.Reshape(flat, 2, 3);
        Assert.Equal(samples[0], round[0]);
        Assert.Equal(samples[1], round[1]);
    }

    [Fact]
    public void Decode_RejectsTruncatedData()
    {
        Assert.Throws<WfdbFormatException>(() =>
            WfdbSignalCodec.Decode(WfdbSignalCodec.Format16, new byte[3], 0, 1, 2));
    }
}
