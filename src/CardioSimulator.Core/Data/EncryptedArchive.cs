using System.IO.Compression;
using System.Text;

namespace CardioSimulator.Core.Data;

/// <summary>
/// An encrypted content pack (<c>*.pak</c>) opened for reading. The pack is a ZIP archive encrypted
/// with <see cref="ContentCrypto"/>; entries are exposed without ever writing plaintext to disk.
///
/// <para><b>Memory.</b> A <c>CSP2</c> pack is read lazily: <see cref="ZipArchive"/> sits on a
/// <see cref="ChunkedPackStream"/> that decrypts 64 KiB chunks on demand straight from the file, so
/// steady cost is the ZIP central directory plus a small chunk cache — a few tens of MB even for a
/// multi-gigabyte dataset, and there is no <see cref="Array.MaxLength"/> ceiling. A legacy
/// <c>CSP1</c> pack cannot be decrypted piecewise, so it still costs ~2x its size at open and stays
/// resident; that path exists only so packs already in the field keep working.</para>
///
/// <para>Two lookup styles are offered because the two datasets are laid out differently: pathology
/// packs are flat (<c>manifest.txt</c> + <c>&lt;id&gt;.dat</c> at the root, matched by
/// <see cref="ReadByName"/>), while course packs preserve their directory tree
/// (<c>&lt;courseId&gt;/lectures/&lt;id&gt;.&lt;lang&gt;.html</c>, matched by <see cref="ReadPath"/>).</para>
///
/// <para><b>Threading.</b> <see cref="ZipArchive"/> is not thread-safe and cannot have two entry
/// streams open at once, and the chunk cache is shared mutable state, so every public member here
/// serialises on one lock for its whole body — including the entry-stream reads it triggers.
/// Uncontended locks are nanoseconds next to inflate, so this costs nothing measurable.</para>
///
/// <para><b>Corruption surfaces later than it used to.</b> With CSP1 a damaged pack failed at
/// <see cref="Open"/>. A CSP2 pack authenticates its header at open but each chunk only when read,
/// so a damaged chunk throws from a <c>Read*</c> call instead. Callers already treat read failures as
/// "entry missing" (they catch and return null), which keeps that contained.</para>
/// </summary>
public sealed class EncryptedArchive : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly ZipArchive _zip;
    private readonly object _gate = new();
    private Dictionary<string, ZipArchiveEntry>? _byName;

    private EncryptedArchive(Stream plaintext)
    {
        _stream = plaintext;
        try
        {
            _zip = new ZipArchive(_stream, ZipArchiveMode.Read);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens the pack at <paramref name="packPath"/>. A CSP2 pack is mapped lazily; a legacy
    /// CSP1 pack is decrypted whole. Throws if the file is missing, is not a pack, or fails to
    /// authenticate.</summary>
    public static EncryptedArchive Open(string packPath)
    {
        // FileShare.Read: the handle is held for the archive's lifetime, so deny writers. Letting a
        // writer mutate the pack underneath a lazy reader would surface as tag-mismatch errors mid-
        // session; a clean sharing violation at the writer is the better failure.
        var handle = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lazy = false;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            if (RandomAccess.Read(handle, magic, 0) == 4 && ContentCrypto.LooksLikeV2(magic))
            {
                var stream = ContentCrypto.OpenDecryptingRead(handle); // takes ownership of the handle
                lazy = true;
                return new EncryptedArchive(stream);
            }
        }
        catch
        {
            if (!lazy) handle.Dispose();
            throw;
        }
        handle.Dispose();
        return new EncryptedArchive(new MemoryStream(ContentCrypto.Decrypt(File.ReadAllBytes(packPath)), writable: false));
    }

    /// <summary>Decrypts and opens a pack held in memory.</summary>
    public static EncryptedArchive OpenBytes(byte[] packBytes) =>
        ContentCrypto.LooksLikeV2(packBytes)
            ? new EncryptedArchive(ContentCrypto.OpenDecryptingRead(packBytes))
            : new EncryptedArchive(new MemoryStream(ContentCrypto.Decrypt(packBytes), writable: false));

    /// <summary>All entry paths in the archive (forward-slash separated, as stored).</summary>
    public IEnumerable<string> EntryPaths
    {
        // Materialised inside the lock: the caller enumerates at its leisure, and ZipArchive must not
        // be touched concurrently.
        get { lock (_gate) return _zip.Entries.Select(e => e.FullName).ToList(); }
    }

    /// <summary>Reads an entry by its exact archive path (e.g. <c>cardio-101/course.txt</c>).</summary>
    public byte[]? ReadPath(string fullName)
    {
        lock (_gate)
        {
            var entry = _zip.GetEntry(fullName)
                        ?? _zip.GetEntry(fullName.Replace('\\', '/'));
            return entry is null ? null : ReadEntry(entry);
        }
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
        lock (_gate)
        {
            // Indexed rather than scanned: a 45k-entry pack turned every lookup into a linear walk,
            // so loading a manifest was quadratic.
            _byName ??= BuildByName();
            return _byName.TryGetValue(name, out var entry) ? ReadEntry(entry) : null;
        }
    }

    /// <summary>Name → entry index. Preserves the previous semantics exactly: case-insensitive, and
    /// first match wins when several directories hold the same file name.</summary>
    private Dictionary<string, ZipArchiveEntry> BuildByName()
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory
            map.TryAdd(entry.Name, entry);
        }
        return map;
    }

    /// <summary>UTF-8 text of the first entry matching <paramref name="name"/> by file name.</summary>
    public string? ReadByNameText(string name) =>
        ReadByName(name) is { } bytes ? Utf8.GetString(bytes) : null;

    /// <summary>File names (no directory prefix) of every entry whose name ends with
    /// <paramref name="extension"/> (e.g. <c>.dat</c>). Directory entries are skipped.</summary>
    public IEnumerable<string> FileNamesWithExtension(string extension)
    {
        lock (_gate)
        {
            return _zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) &&
                            e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Name)
                .ToList();
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        // Length is the exact uncompressed size in Read mode, so read straight into the final array
        // rather than growing a MemoryStream and copying it (which cost 2x per entry).
        var buffer = new byte[entry.Length];
        using var s = entry.Open();
        s.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _zip.Dispose();
            _stream.Dispose();
        }
    }
}
