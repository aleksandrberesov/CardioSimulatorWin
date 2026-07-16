using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Builds a plain, unencrypted ZIP of the dataset with every <c>.dat</c> in the original text format —
/// what the classroom server ingests. This is deliberately *not* the pack format: it is the wire
/// format for <see cref="IContentPackExportable"/> sources going out over TCP.
///
/// <para>Two conversions happen on the way out. The source is a pack, so its <c>.dat</c> entries are
/// <c>CSD1</c> delta-binary and get decoded back to text here; and the source is the live (overlay)
/// source, so instructor edits are already merged over the base by the time entries are enumerated.
/// Entries that are already text — <c>manifest.txt</c>, <c>groups.txt</c>, an overlay <c>.dat</c>
/// saved as text — pass through verbatim rather than being re-serialized, so their formatting is
/// preserved exactly.</para>
///
/// <para>Text is roughly 3x the size of the delta-binary it replaces once zipped, and the result has
/// no encryption at all. Both are inherent to the format the server expects.</para>
/// </summary>
public static class PlainTextZipWriter
{
    /// <summary>
    /// Streams the dataset to <paramref name="destPath"/> as a text ZIP, one entry at a time, so peak
    /// memory stays at a single entry regardless of dataset size. <paramref name="leadOrder"/> should
    /// be the manifest's order — the packer wrote the binary with it, so passing the same order back
    /// makes this the exact inverse of that step.
    /// </summary>
    public static void WriteTextZip(IContentPackExportable source, IReadOnlyList<Lead> leadOrder, string destPath)
    {
        // Write beside the target and rename, so an interrupted write cannot leave a truncated zip at
        // the destination looking valid.
        var tmp = destPath + ".tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in source.ExportEntries())
                {
                    var payload = ToTextBytes(name, bytes, leadOrder);
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(payload, 0, payload.Length);
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

    private static byte[] ToTextBytes(string name, byte[] bytes, IReadOnlyList<Lead> leadOrder)
    {
        if (!name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return bytes;
        if (!PathologyParser.HasBinaryMagic(bytes)) return bytes;

        var file = PathologyParser.ParsePathology(bytes);
        return Encoding.UTF8.GetBytes(PathologyParser.SerializePathology(file, leadOrder));
    }
}
