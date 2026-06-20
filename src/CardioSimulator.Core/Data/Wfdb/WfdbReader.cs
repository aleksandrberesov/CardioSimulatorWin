namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>
/// Reads complete WFDB records (header + signal files) into <see cref="WfdbRecord"/>.
///
/// Use <see cref="ReadRecord(string)"/> for on-disk records, or
/// <see cref="ReadRecord(WfdbHeader, Func{string, byte[]})"/> with a custom resolver to read from
/// any source (e.g. an HTTP download — see <c>PhysioNetClient</c>).
/// </summary>
public static class WfdbReader
{
    /// <summary>Reads only the header for the record at <paramref name="headerPath"/> (a <c>.hea</c> file).</summary>
    public static WfdbHeader ReadHeader(string headerPath)
    {
        var text = File.ReadAllText(headerPath);
        var header = WfdbHeaderParser.Parse(text);
        // Prefer the on-disk filename as the record name (headers occasionally disagree).
        var nameFromFile = Path.GetFileNameWithoutExtension(headerPath);
        return string.IsNullOrEmpty(nameFromFile) ? header : header with { RecordName = nameFromFile };
    }

    /// <summary>
    /// Reads the full record at <paramref name="headerPath"/>. Signal files are resolved relative to
    /// the header's directory.
    /// </summary>
    public static WfdbRecord ReadRecord(string headerPath)
    {
        var header = ReadHeader(headerPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? ".";
        return ReadRecord(header, name => File.ReadAllBytes(Path.Combine(dir, name)));
    }

    /// <summary>
    /// Decodes a record from an already-parsed <paramref name="header"/>, fetching each referenced
    /// signal file through <paramref name="signalFileResolver"/> (called once per distinct filename).
    /// </summary>
    public static WfdbRecord ReadRecord(WfdbHeader header, Func<string, byte[]> signalFileResolver)
    {
        var nsig = header.Signals.Count;
        if (nsig == 0)
            return new WfdbRecord(header, Array.Empty<int[]>());

        var samples = new int[nsig][];

        // Group signal indices by the file that stores them, preserving declaration order.
        var byFile = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();
        for (var i = 0; i < nsig; i++)
        {
            var file = header.Signals[i].FileName;
            if (!byFile.TryGetValue(file, out var list))
            {
                list = new List<int>();
                byFile[file] = list;
                fileOrder.Add(file);
            }
            list.Add(i);
        }

        foreach (var file in fileOrder)
        {
            var globalIndices = byFile[file];
            var bytes = signalFileResolver(file);
            var channelsInFile = globalIndices.Count;
            int[][] decoded = IsMatFile(file)
                ? DecodeMat(bytes, channelsInFile, header.NumberOfSamples)
                : DecodeDat(header.Signals[globalIndices[0]], bytes, channelsInFile, header.NumberOfSamples);

            for (var local = 0; local < channelsInFile; local++)
                samples[globalIndices[local]] = decoded[local];
        }

        return new WfdbRecord(header, samples);
    }

    private static bool IsMatFile(string fileName) =>
        fileName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);

    private static int[][] DecodeMat(byte[] bytes, int channels, long declaredSamples)
    {
        var matrix = MatlabLevel4.ReadMatrix(bytes);
        if (matrix.Rows != channels)
            throw new WfdbFormatException(
                $"MAT matrix has {matrix.Rows} rows but the header declares {channels} signal(s) in this file.");
        if (declaredSamples > 0 && matrix.Cols != declaredSamples)
            throw new WfdbFormatException(
                $"MAT matrix has {matrix.Cols} columns but the header declares {declaredSamples} samples.");

        // Column-major [rows x cols] is identical to frame-interleaved [channels x samples].
        return WfdbSignalCodec.Reshape(matrix.DataColumnMajor, matrix.Rows, matrix.Cols);
    }

    private static int[][] DecodeDat(WfdbSignalSpec firstSpec, byte[] bytes, int channels, long declaredSamples)
    {
        var samplesPerChannel = declaredSamples > 0
            ? declaredSamples
            : InferSampleCount(firstSpec.Format, bytes.Length - firstSpec.ByteOffset, channels);

        return WfdbSignalCodec.Decode(firstSpec.Format, bytes, firstSpec.ByteOffset, channels, samplesPerChannel);
    }

    private static long InferSampleCount(int format, long dataBytes, int channels)
    {
        if (dataBytes <= 0 || channels <= 0) return 0;
        return format switch
        {
            WfdbSignalCodec.Format16 or WfdbSignalCodec.Format61 => dataBytes / (2L * channels),
            WfdbSignalCodec.Format24 => dataBytes / (3L * channels),
            WfdbSignalCodec.Format32 => dataBytes / (4L * channels),
            WfdbSignalCodec.Format80 => dataBytes / channels,
            WfdbSignalCodec.Format212 => dataBytes * 2L / (3L * channels),
            _ => throw new WfdbFormatException(
                $"Cannot infer sample count for format {format}; header must declare the sample count.")
        };
    }
}
