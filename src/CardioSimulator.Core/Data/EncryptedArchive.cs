using System.IO.Compression;
using System.Text;

namespace CardioSimulator.Core.Data;

/// <summary>
/// An encrypted content pack (<c>*.pak</c>) opened for reading entirely in memory. The pack is a
/// ZIP archive encrypted with <see cref="ContentCrypto"/>; this decrypts it into a
/// <see cref="MemoryStream"/> and exposes the entries without ever writing plaintext to disk.
///
/// <para>The bundled dataset is small (well under a few MB compressed), so holding the whole
/// decrypted ZIP in memory for the app's lifetime is cheap; individual entries are decompressed on
/// demand by <see cref="ZipArchive"/>. Dispose to release the buffer.</para>
///
/// <para>Two lookup styles are offered because the two datasets are laid out differently:
/// pathology packs are flat (<c>manifest.txt</c> + <c>&lt;id&gt;.dat</c> at the root, matched by
/// <see cref="ReadByName"/>), while course packs preserve their directory tree
/// (<c>&lt;courseId&gt;/lectures/&lt;id&gt;.&lt;lang&gt;.html</c>, matched by <see cref="ReadByPath"/>).</para>
/// </summary>
public sealed class EncryptedArchive : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly MemoryStream _stream;
    private readonly ZipArchive _zip;

    private EncryptedArchive(byte[] zipBytes)
    {
        // Not writable and not resizable: the buffer is exactly the decrypted ZIP.
        _stream = new MemoryStream(zipBytes, writable: false);
        _zip = new ZipArchive(_stream, ZipArchiveMode.Read);
    }

    /// <summary>Decrypts and opens the pack at <paramref name="packPath"/>. Throws if the file is
    /// missing, not a pack, or fails authentication.</summary>
    public static EncryptedArchive Open(string packPath) =>
        new(ContentCrypto.Decrypt(File.ReadAllBytes(packPath)));

    /// <summary>Decrypts and opens a pack held in memory.</summary>
    public static EncryptedArchive OpenBytes(byte[] packBytes) =>
        new(ContentCrypto.Decrypt(packBytes));

    /// <summary>All entry paths in the archive (forward-slash separated, as stored).</summary>
    public IEnumerable<string> EntryPaths => _zip.Entries.Select(e => e.FullName);

    /// <summary>Reads an entry by its exact archive path (e.g. <c>cardio-101/course.txt</c>).</summary>
    public byte[]? ReadPath(string fullName)
    {
        var entry = _zip.GetEntry(fullName)
                    ?? _zip.GetEntry(fullName.Replace('\\', '/'));
        return entry is null ? null : ReadEntry(entry);
    }

    /// <summary>UTF-8 text of an entry addressed by exact path, or null if absent.</summary>
    public string? ReadPathText(string fullName) =>
        ReadPath(fullName) is { } bytes ? Utf8.GetString(bytes) : null;

    /// <summary>
    /// Reads the first entry whose <i>file name</i> (ignoring any directory prefix) equals
    /// <paramref name="name"/>. Mirrors the flattening the old on-disk extractor applied to
    /// pathology packs.
    /// </summary>
    public byte[]? ReadByName(string name)
    {
        var entry = _zip.Entries.FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ReadEntry(entry);
    }

    /// <summary>UTF-8 text of the first entry matching <paramref name="name"/> by file name.</summary>
    public string? ReadByNameText(string name) =>
        ReadByName(name) is { } bytes ? Utf8.GetString(bytes) : null;

    /// <summary>File names (no directory prefix) of every entry whose name ends with
    /// <paramref name="extension"/> (e.g. <c>.dat</c>). Directory entries are skipped.</summary>
    public IEnumerable<string> FileNamesWithExtension(string extension) =>
        _zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) &&
                        e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name);

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream((int)Math.Max(0, entry.Length));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
