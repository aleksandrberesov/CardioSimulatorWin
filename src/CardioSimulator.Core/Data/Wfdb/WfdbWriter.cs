using System.Text;

namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>How a record's samples are stored on disk.</summary>
public enum WfdbStorage
{
    /// <summary>A raw <c>.dat</c> file in format 16 (16-bit little-endian).</summary>
    Dat,

    /// <summary>A MATLAB v4 <c>.mat</c> file (matrix <c>val</c>), as produced by <c>wfdb2mat</c>.</summary>
    Mat,
}

/// <summary>
/// Writes <see cref="WfdbRecord"/>s back out as a WFDB header plus a signal file. Samples are stored
/// in format 16 (either a raw <c>.dat</c> or a <c>.mat</c>); checksums and initial values are
/// recomputed from the samples so the output is internally consistent.
/// </summary>
public static class WfdbWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="record"/> to <paramref name="directory"/> as <c>&lt;name&gt;.hea</c> plus a
    /// signal file. Returns the header actually written (with updated storage fields, checksums, and
    /// initial values).
    /// </summary>
    public static WfdbHeader WriteRecord(
        WfdbRecord record,
        string directory,
        WfdbStorage storage = WfdbStorage.Dat,
        string? recordName = null)
    {
        var name = recordName ?? record.Header.RecordName;
        var (header, signalFileName, signalBytes) = Build(record, name, storage);

        Directory.CreateDirectory(directory);
        WriteAtomic(Path.Combine(directory, signalFileName), signalBytes);
        WriteAtomic(Path.Combine(directory, name + ".hea"), Utf8NoBom.GetBytes(WfdbHeaderParser.Serialize(header)));
        return header;
    }

    /// <summary>
    /// Produces the header and signal-file bytes for <paramref name="record"/> without touching disk —
    /// used by the on-disk writer and by network round-trips. Returns the updated header, the signal
    /// file name, and its encoded bytes.
    /// </summary>
    public static (WfdbHeader Header, string SignalFileName, byte[] SignalBytes) Build(
        WfdbRecord record,
        string recordName,
        WfdbStorage storage)
    {
        var channels = record.ChannelCount;
        if (channels == 0)
            throw new WfdbFormatException("Cannot write a record with no signals.");

        var sampleCount = record.SampleCount;
        for (var c = 0; c < channels; c++)
        {
            if (record.Samples[c].Length != sampleCount)
                throw new WfdbFormatException("All channels must have the same number of samples.");
        }

        var isMat = storage == WfdbStorage.Mat;
        var signalFileName = recordName + (isMat ? ".mat" : ".dat");
        var byteOffset = isMat ? MatlabLevel4.DataOffset(WfdbConstants.MatVariableName) : 0L;

        var signalBytes = isMat
            ? MatlabLevel4.WriteInt16Matrix(WfdbConstants.MatVariableName, channels, sampleCount, WfdbSignalCodec.Flatten(record.Samples))
            : WfdbSignalCodec.Encode(WfdbSignalCodec.Format16, record.Samples);

        var specs = new List<WfdbSignalSpec>(channels);
        for (var c = 0; c < channels; c++)
        {
            var source = c < record.Header.Signals.Count ? record.Header.Signals[c] : DefaultSpec(c);
            specs.Add(source with
            {
                FileName = signalFileName,
                Format = WfdbSignalCodec.Format16,
                SamplesPerFrame = 1,
                Skew = 0,
                ByteOffset = byteOffset,
                InitialValue = sampleCount > 0 ? record.Samples[c][0] : 0,
                Checksum = ComputeChecksum(record.Samples[c]),
                BlockSize = 0,
            });
        }

        var header = record.Header with
        {
            RecordName = recordName,
            NumberOfSignals = channels,
            NumberOfSamples = sampleCount,
            Signals = specs,
        };

        return (header, signalFileName, signalBytes);
    }

    /// <summary>
    /// WFDB checksum: the low 16 bits of the sum of all samples, interpreted as a signed value
    /// (matching the values written by the WFDB tools).
    /// </summary>
    public static int ComputeChecksum(int[] samples)
    {
        var sum = 0;
        foreach (var s in samples) sum = unchecked(sum + s);
        return unchecked((short)sum);
    }

    private static WfdbSignalSpec DefaultSpec(int channel) => new()
    {
        FileName = "",
        Gain = 1000,
        Units = WfdbConstants.DefaultUnits,
        AdcResolution = 16,
        Description = $"ch{channel}",
    };

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
