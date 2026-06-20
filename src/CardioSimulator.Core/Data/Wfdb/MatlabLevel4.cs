using System.Buffers.Binary;

namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>
/// Minimal reader/writer for MATLAB Level-4 (v4) MAT-files, the format produced by the WFDB
/// <c>wfdb2mat</c> tool for <c>.mat</c> signal files. A v4 file is a 20-byte header
/// (5 little-endian int32: type, rows, cols, imagf, namelen), the matrix name (namelen bytes,
/// NUL-terminated), then the matrix data in column-major order.
///
/// The <c>type</c> word encodes <c>M*1000 + O*100 + P*10 + T</c>; WFDB writes <c>30</c>
/// (M=0 little-endian IEEE, P=3 = 16-bit signed integer, T=0 = full matrix) for a matrix
/// named <c>val</c> shaped <c>[channels x samples]</c>.
/// </summary>
public static class MatlabLevel4
{
    private const int HeaderBytes = 20;

    // P field of the type word — the numeric class of the stored data.
    private const int PDouble = 0;
    private const int PSingle = 1;
    private const int PInt32 = 2;
    private const int PInt16 = 3;
    private const int PUInt16 = 4;
    private const int PUInt8 = 5;

    /// <summary>A decoded MAT matrix: name, dimensions, and data flattened in column-major order.</summary>
    public readonly record struct Matrix(string Name, int Rows, int Cols, int[] DataColumnMajor);

    /// <summary>
    /// Reads the first (and, for WFDB files, only) matrix from a MATLAB v4 buffer.
    /// Supports the common numeric classes; values are returned as <see cref="int"/>
    /// (non-integer classes are truncated toward zero).
    /// </summary>
    public static Matrix ReadMatrix(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
            throw new WfdbFormatException("MAT file is shorter than its 20-byte header.");

        var type = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var rows = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var cols = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var imag = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        var nameLen = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);

        var m = type / 1000;          // byte order: 0 = little-endian IEEE
        var p = (type / 10) % 10;     // numeric class
        var t = type % 10;            // 0 = full matrix

        if (m != 0)
            throw new WfdbFormatException($"Unsupported MAT byte order (M={m}); only little-endian is supported.");
        if (t != 0)
            throw new WfdbFormatException($"Unsupported MAT matrix type (T={t}); only full matrices are supported.");
        if (rows < 0 || cols < 0 || nameLen < 0)
            throw new WfdbFormatException("MAT file has invalid dimensions.");

        var dataStart = HeaderBytes + nameLen;
        if (dataStart > bytes.Length)
            throw new WfdbFormatException("MAT file name extends past end of buffer.");

        var name = ReadName(bytes.Slice(HeaderBytes, nameLen));
        var count = checked(rows * cols);
        var elemSize = ElementSize(p);
        var realBytes = checked(count * elemSize);
        if (dataStart + (long)realBytes * (imag != 0 ? 2 : 1) > bytes.Length)
            throw new WfdbFormatException("MAT file data extends past end of buffer.");

        var data = ReadData(bytes.Slice(dataStart, realBytes), p, count);
        return new Matrix(name, rows, cols, data);
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        if (end < 0) end = raw.Length;
        return System.Text.Encoding.ASCII.GetString(raw[..end]);
    }

    private static int ElementSize(int p) => p switch
    {
        PDouble => 8,
        PSingle => 4,
        PInt32 => 4,
        PInt16 => 2,
        PUInt16 => 2,
        PUInt8 => 1,
        _ => throw new WfdbFormatException($"Unsupported MAT numeric class (P={p}).")
    };

    private static int[] ReadData(ReadOnlySpan<byte> raw, int p, int count)
    {
        var data = new int[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = p switch
            {
                PDouble => (int)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(raw[(i * 8)..])),
                PSingle => (int)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(raw[(i * 4)..])),
                PInt32 => BinaryPrimitives.ReadInt32LittleEndian(raw[(i * 4)..]),
                PInt16 => BinaryPrimitives.ReadInt16LittleEndian(raw[(i * 2)..]),
                PUInt16 => BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]),
                PUInt8 => raw[i],
                _ => throw new WfdbFormatException($"Unsupported MAT numeric class (P={p}).")
            };
        }
        return data;
    }

    /// <summary>
    /// Writes a <c>[rows x cols]</c> int16 matrix as a MATLAB v4 buffer, matching wfdb2mat output.
    /// <paramref name="dataColumnMajor"/> must be laid out column-major: element (r, c) at index
    /// <c>c * rows + r</c>. Values are range-checked to fit in int16.
    /// </summary>
    public static byte[] WriteInt16Matrix(string name, int rows, int cols, ReadOnlySpan<int> dataColumnMajor)
    {
        var count = checked(rows * cols);
        if (dataColumnMajor.Length != count)
            throw new ArgumentException($"Expected {count} values for a {rows}x{cols} matrix, got {dataColumnMajor.Length}.", nameof(dataColumnMajor));

        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        var nameLen = nameBytes.Length + 1; // include NUL terminator
        var total = HeaderBytes + nameLen + count * 2;
        var buffer = new byte[total];
        var span = buffer.AsSpan();

        const int type = 30; // M=0 (LE/IEEE), O=0, P=3 (int16), T=0 (full)
        BinaryPrimitives.WriteInt32LittleEndian(span, type);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], rows);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], cols);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 0); // imagf
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], nameLen);

        nameBytes.CopyTo(span[HeaderBytes..]);
        span[HeaderBytes + nameBytes.Length] = 0;

        var dataStart = HeaderBytes + nameLen;
        for (var i = 0; i < count; i++)
        {
            var v = dataColumnMajor[i];
            if (v < short.MinValue || v > short.MaxValue)
                throw new WfdbFormatException($"Sample {v} does not fit in int16 (format 16 / MAT).");
            BinaryPrimitives.WriteInt16LittleEndian(span[(dataStart + i * 2)..], (short)v);
        }

        return buffer;
    }

    /// <summary>Byte offset to the first sample for a v4 matrix with the given variable name.</summary>
    public static long DataOffset(string name) => HeaderBytes + name.Length + 1;
}
