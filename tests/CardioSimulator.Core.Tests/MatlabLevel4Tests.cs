using CardioSimulator.Core.Data.Wfdb;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class MatlabLevel4Tests
{
    [Fact]
    public void WriteThenRead_RoundTripsInt16Matrix()
    {
        // 3 channels x 4 samples, column-major (channel-major within each column).
        var rows = 3;
        var cols = 4;
        var data = new[]
        {
            // col 0      col 1        col 2          col 3
            1, 2, 3,      10, 20, 30,  -1, -2, -3,    100, 200, 300,
        };

        var bytes = MatlabLevel4.WriteInt16Matrix("val", rows, cols, data);
        var matrix = MatlabLevel4.ReadMatrix(bytes);

        Assert.Equal("val", matrix.Name);
        Assert.Equal(rows, matrix.Rows);
        Assert.Equal(cols, matrix.Cols);
        Assert.Equal(data, matrix.DataColumnMajor);
    }

    [Fact]
    public void DataOffset_MatchesWfdbConvention()
    {
        // 20-byte header + "val\0" = 24, exactly the "+24" offset in the bundled headers.
        Assert.Equal(24, MatlabLevel4.DataOffset("val"));

        var bytes = MatlabLevel4.WriteInt16Matrix("val", 1, 1, new[] { 7 });
        Assert.Equal(24 + 2, bytes.Length);
    }

    [Fact]
    public void WriteInt16Matrix_RejectsOutOfRangeValues()
    {
        Assert.Throws<WfdbFormatException>(() =>
            MatlabLevel4.WriteInt16Matrix("val", 1, 1, new[] { 40000 }));
    }

    [Fact]
    public void ReadMatrix_DecodesHandBuiltHeader()
    {
        // Mirror the real .mat layout: type=30 (int16 LE full), 1x2 matrix named "val", values 5 and -3.
        var bytes = new byte[24 + 4];
        WriteI32(bytes, 0, 30);
        WriteI32(bytes, 4, 1);   // rows
        WriteI32(bytes, 8, 2);   // cols
        WriteI32(bytes, 12, 0);  // imagf
        WriteI32(bytes, 16, 4);  // namelen
        bytes[20] = (byte)'v'; bytes[21] = (byte)'a'; bytes[22] = (byte)'l'; bytes[23] = 0;
        WriteI16(bytes, 24, 5);
        WriteI16(bytes, 26, -3);

        var matrix = MatlabLevel4.ReadMatrix(bytes);

        Assert.Equal(1, matrix.Rows);
        Assert.Equal(2, matrix.Cols);
        Assert.Equal(new[] { 5, -3 }, matrix.DataColumnMajor);
    }

    private static void WriteI32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteI16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }
}
