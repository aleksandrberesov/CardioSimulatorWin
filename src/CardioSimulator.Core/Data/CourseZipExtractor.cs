using System;
using System.IO;
using System.IO.Compression;

namespace CardioSimulator.Core.Data;

public static class CourseZipExtractor
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
            
            var rootCanonical = Path.GetFullPath(targetDir);
            var rootPrefix = rootCanonical + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                var outFile = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                
                // Guard against zip-slip: reject entries that resolve outside targetDir
                if (outFile != rootCanonical && !outFile.StartsWith(rootPrefix))
                {
                    continue;
                }

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    Directory.CreateDirectory(outFile);
                }
                else
                {
                    var dir = Path.GetDirectoryName(outFile);
                    if (dir != null && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    entry.ExtractToFile(outFile, overwrite: true);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
