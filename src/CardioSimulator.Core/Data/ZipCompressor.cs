using System.IO.Compression;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Packs a dataset directory into a flat ZIP (all files at the root). Used for
/// the explicit export flow and the TCP upload snapshot.
/// </summary>
public static class ZipCompressor
{
    /// <summary>Zips the top-level files of <paramref name="sourceDir"/> to <paramref name="destPath"/>.</summary>
    public static bool Zip(string sourceDir, string destPath)
    {
        try
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            using var fs = File.Create(destPath);
            WriteArchive(sourceDir, fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Zips <paramref name="sourceDir"/> to a temp file; returns its path or null.</summary>
    public static string? ZipToTemp(string sourceDir, string fileName = "upload.zip")
    {
        var tmp = Path.Combine(Path.GetTempPath(), fileName);
        return Zip(sourceDir, tmp) ? tmp : null;
    }

    private static void WriteArchive(string sourceDir, Stream output)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        if (!Directory.Exists(sourceDir)) return;
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }
    }
}
