using System.IO.Compression;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Extracts a <c>Pathologies.zip</c> into a target directory, flattening any
/// nested directories (UTF-8, no charset detection — per
/// <c>docs/ecg-rendering-pipeline.md</c> Stage 1). The target directory is
/// cleared first.
/// </summary>
public static class ZipExtractor
{
    public static bool Extract(string zipPath, string targetDir)
    {
        try
        {
            using var fs = File.OpenRead(zipPath);
            return Extract(fs, targetDir);
        }
        catch
        {
            return false;
        }
    }

    public static bool Extract(Stream zipStream, string targetDir)
    {
        try
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                // ZipArchiveEntry.Name is the file name without directory prefix;
                // empty for directory entries. This flattens nested folders.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.Combine(targetDir, entry.Name);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
