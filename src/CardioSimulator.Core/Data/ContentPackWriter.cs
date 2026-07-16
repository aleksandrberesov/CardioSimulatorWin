using System.IO.Compression;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Builds a distributable encrypted content pack (<c>*.pak</c>) from an
/// <see cref="IContentPackExportable"/> source, without ever writing a plaintext dataset to disk.
/// Used by the Full-edition Settings "Export" action: the exported pak has the same protection as the
/// shipped <c>Assets/*.pak</c> (AES-256-GCM, app-decryptable), letting an instructor relocate their
/// edited dataset without producing a plaintext copy.
/// </summary>
public static class ContentPackWriter
{
    /// <summary>
    /// Streams the pack straight to <paramref name="destPath"/>: entries are zipped and encrypted as
    /// they are produced, so peak memory is a couple of buffers regardless of dataset size.
    ///
    /// <para>Prefer this over <see cref="BuildEncryptedPack"/> for anything user-sized. Building a
    /// pack in memory needs roughly four full-size copies (the ZIP buffer, its <c>ToArray</c>, the
    /// ciphertext and the output array) and cannot exceed <see cref="Array.MaxLength"/> at all.</para>
    /// </summary>
    public static void WriteEncryptedPack(IContentPackExportable source, string destPath)
    {
        // Write beside the target and rename, so an interrupted export cannot leave a truncated .pak
        // at the destination looking valid.
        var tmp = destPath + ".tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // Dispose order matters: the ZipArchive must finish (central directory written)
                // before the pack stream flushes its final chunk and back-patches the header.
                using (var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true))
                {
                    using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
                    WriteEntries(source, zip);
                }
            }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Builds a pack entirely in memory. Retained for small sources (tests, in-memory callers); it
    /// holds the whole pack plus a copy, so use <see cref="WriteEncryptedPack"/> for a real dataset.
    /// </summary>
    public static byte[] BuildEncryptedPack(IContentPackExportable source)
    {
        using var ms = new MemoryStream();
        using (var pack = ContentCrypto.CreateEncryptingWrite(ms, leaveOpen: true))
        {
            using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
            WriteEntries(source, zip);
        }
        return ms.ToArray();
    }

    private static void WriteEntries(IContentPackExportable source, ZipArchive zip)
    {
        foreach (var (name, bytes) in source.ExportEntries())
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }
    }
}
