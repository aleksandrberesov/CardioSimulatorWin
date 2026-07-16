using System.IO;
using System.IO.Compression;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Packs a dataset directory recursively into a ZIP archive, maintaining the relative directory structure.
/// Used for the explicit export flow and the TCP upload snapshot.
/// </summary>
public static class ZipCompressor
{
    /// <summary>Zips the files under <paramref name="sourceDir"/> recursively to <paramref name="destPath"/>.</summary>
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

    /// <summary>
    /// Zips <paramref name="sourceDir"/> recursively into an in-memory buffer; returns the bytes, or
    /// null if the directory is missing/empty or on error. Used by the TCP upload so no plaintext
    /// zip is ever written to <c>%TEMP%</c>.
    /// </summary>
    public static byte[]? ZipToBytes(string sourceDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return null;
            using var ms = new MemoryStream();
            WriteArchive(sourceDir, ms);
            var bytes = ms.ToArray();
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteArchive(string sourceDir, Stream output)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        if (!Directory.Exists(sourceDir)) return;

        var fullSourceDir = Path.GetFullPath(sourceDir);
        foreach (var file in Directory.GetFiles(fullSourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(fullSourceDir, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
        }
    }
}
