using System.IO.Compression;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Progress snapshot reported by <see cref="ZipExtractor"/> while unpacking:
/// <paramref name="Done"/> of <paramref name="Total"/> archive entries processed,
/// with the name of the most recently extracted entry.
/// </summary>
public readonly record struct ZipProgress(int Done, int Total, string? CurrentEntry);

/// <summary>
/// Extracts a <c>Pathologies.zip</c> into a target directory, flattening any
/// nested directories (UTF-8, no charset detection — per
/// <c>docs/ecg-rendering-pipeline.md</c> Stage 1). The target directory is
/// cleared first. An optional <see cref="IProgress{T}"/> receives extraction
/// progress so the UI can show a loading status bar, and an optional
/// <see cref="CancellationToken"/> lets the user abort: on cancellation the
/// (partial) target directory is removed and <see cref="OperationCanceledException"/>
/// is thrown so the caller can revert.
/// </summary>
public static class ZipExtractor
{
    public static bool Extract(
        string zipPath, string targetDir,
        IProgress<ZipProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var fs = File.OpenRead(zipPath);
            return Extract(fs, targetDir, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static bool Extract(
        Stream zipStream, string targetDir,
        IProgress<ZipProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var total = archive.Entries.Count;
            // Report at most ~100 times to avoid flooding the UI thread on large datasets.
            var step = Math.Max(1, total / 100);
            var done = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ZipArchiveEntry.Name is the file name without directory prefix;
                // empty for directory entries. This flattens nested folders.
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    var dest = Path.Combine(targetDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
                done++;
                if (progress is not null && (done == total || done % step == 0))
                {
                    progress.Report(new ZipProgress(done, total, entry.Name));
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // Don't leave a half-extracted dataset behind for the caller to load.
            try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort cleanup */ }
            throw;
        }
        catch
        {
            return false;
        }
    }
}
