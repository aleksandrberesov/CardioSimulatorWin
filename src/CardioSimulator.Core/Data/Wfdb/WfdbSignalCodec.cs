using System.Buffers.Binary;

namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>
/// Encodes and decodes WFDB sample storage formats. Samples for all channels are stored
/// frame-interleaved (sample 0 of every channel, then sample 1 of every channel, ...), which is
/// also the layout of a wfdb2mat <c>[channels x samples]</c> matrix read column-major.
///
/// Supported for reading: 16 (16-bit LE), 61 (16-bit BE), 80 (8-bit offset binary),
/// 212 (12-bit packed), 24 (24-bit LE), 32 (32-bit LE). Writing is supported for format 16.
/// </summary>
public static class WfdbSignalCodec
{
    public const int Format8 = 8;
    public const int Format16 = 16;
    public const int Format24 = 24;
    public const int Format32 = 32;
    public const int Format61 = 61;
    public const int Format80 = 80;
    public const int Format212 = 212;

    /// <summary>True when <see cref="Decode"/> understands <paramref name="format"/>.</summary>
    public static bool CanDecode(int format) => format is Format16 or Format61 or Format80 or Format212 or Format24 or Format32;

    /// <summary>True when <see cref="Encode"/> can produce <paramref name="format"/>.</summary>
    public static bool CanEncode(int format) => format is Format16;

    /// <summary>
    /// Decodes <paramref name="channels"/> channels of <paramref name="samplesPerChannel"/> samples
    /// from <paramref name="data"/>, starting at <paramref name="byteOffset"/>. Returns
    /// <c>result[channel][sample]</c>.
    /// </summary>
    public static int[][] Decode(int format, ReadOnlySpan<byte> data, long byteOffset, int channels, long samplesPerChannel)
    {
        if (channels <= 0) return Array.Empty<int[]>();
        if (byteOffset < 0 || byteOffset > data.Length)
            throw new WfdbFormatException($"Signal byte offset {byteOffset} is outside the data ({data.Length} bytes).");

        var total = checked((int)(channels * samplesPerChannel));
        var flat = DecodeFlat(format, data[(int)byteOffset..], total);
        return Reshape(flat, channels, (int)samplesPerChannel);
    }

    /// <summary>Decodes a flat frame-interleaved stream of <paramref name="total"/> samples.</summary>
    public static int[] DecodeFlat(int format, ReadOnlySpan<byte> data, int total)
    {
        return format switch
        {
            Format16 => DecodeFixed(data, total, 2, ReadInt16Le),
            Format61 => DecodeFixed(data, total, 2, ReadInt16Be),
            Format32 => DecodeFixed(data, total, 4, (s) => BinaryPrimitives.ReadInt32LittleEndian(s)),
            Format24 => DecodeFixed(data, total, 3, ReadInt24Le),
            Format80 => DecodeFormat80(data, total),
            Format212 => DecodeFormat212(data, total),
            _ => throw new WfdbFormatException($"Unsupported WFDB read format: {format}.")
        };
    }

    /// <summary>Encodes <c>samples[channel][sample]</c> into a frame-interleaved byte buffer.</summary>
    public static byte[] Encode(int format, int[][] samples)
    {
        if (format != Format16)
            throw new WfdbFormatException($"Unsupported WFDB write format: {format}. Only format 16 is supported for writing .dat files.");

        var channels = samples.Length;
        var perChannel = channels > 0 ? samples[0].Length : 0;
        var buffer = new byte[checked(channels * perChannel * 2)];
        var span = buffer.AsSpan();
        var pos = 0;
        for (var s = 0; s < perChannel; s++)
        {
            for (var c = 0; c < channels; c++)
            {
                var v = samples[c][s];
                if (v < short.MinValue || v > short.MaxValue)
                    throw new WfdbFormatException($"Sample {v} does not fit in int16 (format 16).");
                BinaryPrimitives.WriteInt16LittleEndian(span[pos..], (short)v);
                pos += 2;
            }
        }
        return buffer;
    }

    /// <summary>Reshapes a frame-interleaved flat stream into <c>result[channel][sample]</c>.</summary>
    public static int[][] Reshape(int[] flat, int channels, int samplesPerChannel)
    {
        var result = new int[channels][];
        for (var c = 0; c < channels; c++) result[c] = new int[samplesPerChannel];
        for (var i = 0; i < flat.Length; i++)
        {
            var sample = i / channels;
            var channel = i % channels;
            if (sample < samplesPerChannel) result[channel][sample] = flat[i];
        }
        return result;
    }

    /// <summary>Flattens <c>samples[channel][sample]</c> into a frame-interleaved stream.</summary>
    public static int[] Flatten(int[][] samples)
    {
        var channels = samples.Length;
        var perChannel = channels > 0 ? samples[0].Length : 0;
        var flat = new int[channels * perChannel];
        var i = 0;
        for (var s = 0; s < perChannel; s++)
            for (var c = 0; c < channels; c++)
                flat[i++] = samples[c][s];
        return flat;
    }

    private delegate int ReadSample(ReadOnlySpan<byte> source);

    private static int[] DecodeFixed(ReadOnlySpan<byte> data, int total, int size, ReadSample read)
    {
        var needed = checked(total * size);
        if (data.Length < needed)
            throw new WfdbFormatException($"Signal data too short: need {needed} bytes, have {data.Length}.");
        var result = new int[total];
        for (var i = 0; i < total; i++)
            result[i] = read(data[(i * size)..]);
        return result;
    }

    private static int ReadInt16Le(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt16LittleEndian(s);
    private static int ReadInt16Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt16BigEndian(s);

    private static int ReadInt24Le(ReadOnlySpan<byte> s)
    {
        var v = s[0] | (s[1] << 8) | (s[2] << 16);
        if ((v & 0x800000) != 0) v -= 0x1000000; // sign-extend 24-bit
        return v;
    }

    private static int[] DecodeFormat80(ReadOnlySpan<byte> data, int total)
    {
        if (data.Length < total)
            throw new WfdbFormatException($"Format 80 data too short: need {total} bytes, have {data.Length}.");
        var result = new int[total];
        for (var i = 0; i < total; i++)
            result[i] = data[i] - 128; // 8-bit offset binary
        return result;
    }

    private static int[] DecodeFormat212(ReadOnlySpan<byte> data, int total)
    {
        // Two 12-bit two's-complement samples are packed into three bytes:
        //   byte0 = low 8 bits of sample A
        //   byte1 = (high 4 bits of B in high nibble) | (high 4 bits of A in low nibble)
        //   byte2 = low 8 bits of sample B
        var groups = (total + 1) / 2;
        var needed = groups * 3 - (total % 2 == 1 ? 1 : 0); // last odd sample needs only 2 bytes
        if (data.Length < needed)
            throw new WfdbFormatException($"Format 212 data too short: need {needed} bytes, have {data.Length}.");

        var result = new int[total];
        var bytePos = 0;
        var i = 0;
        while (i < total)
        {
            var b0 = data[bytePos];
            var b1 = data[bytePos + 1];
            var a = ((b1 & 0x0F) << 8) | b0;
            if ((a & 0x800) != 0) a -= 0x1000;
            result[i++] = a;

            if (i < total)
            {
                var b2 = data[bytePos + 2];
                var b = ((b1 & 0xF0) << 4) | b2;
                if ((b & 0x800) != 0) b -= 0x1000;
                result[i++] = b;
                bytePos += 3;
            }
        }
        return result;
    }
}
