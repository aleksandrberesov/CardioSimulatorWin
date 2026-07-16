using System.IO.Compression;
using System.Text;

namespace CardioSimulator.Core.Data;

/// <summary>
/// A small, mutable, AES-256-GCM-encrypted key/value store persisted as a single <c>*.pak</c> file
/// (the writable counterpart of the read-only <see cref="EncryptedArchive"/>). Entries are held in
/// memory as path → bytes and flushed atomically (encrypt whole → <c>.tmp</c> + rename) after every
/// mutation.
///
/// <para>This backs the constructor's writable overlay over a read-only content pack
/// (<see cref="OverlayPathologySource"/> / <see cref="OverlayCourseSource"/>). It MUST stay
/// encrypted at rest: editing or duplicating a bundled item copies <i>decrypted bundle content</i>
/// into the overlay, so a plaintext overlay would let "duplicate everything" reconstruct the whole
/// protected dataset as loose files — exactly what the packs prevent.</para>
///
/// <para>Overlays are small (only the instructor's deltas), so holding everything in memory and
/// re-encrypting on each write is cheap. A corrupt/foreign file is treated as an empty overlay
/// rather than throwing, so a bad file never blocks startup.</para>
/// </summary>
public sealed class WritableEncryptedOverlay
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly Dictionary<string, byte[]> _entries;
    private readonly object _gate = new();

    private WritableEncryptedOverlay(string path, Dictionary<string, byte[]> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Opens the overlay at <paramref name="path"/>, decrypting it into memory, or starts a
    /// fresh empty overlay if the file is absent, corrupt, or not a valid pack.</summary>
    public static WritableEncryptedOverlay OpenOrCreate(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                var zipBytes = ContentCrypto.Decrypt(File.ReadAllBytes(path));
                using var ms = new MemoryStream(zipBytes, writable: false);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory
                    using var s = entry.Open();
                    using var buf = new MemoryStream();
                    s.CopyTo(buf);
                    entries[entry.FullName] = buf.ToArray();
                }
            }
        }
        catch
        {
            entries.Clear(); // unreadable overlay → start empty (never block startup / lose to a throw)
        }
        return new WritableEncryptedOverlay(path, entries);
    }

    public bool Exists(string name)
    {
        lock (_gate) return _entries.ContainsKey(name);
    }

    public byte[]? Read(string name)
    {
        lock (_gate) return _entries.TryGetValue(name, out var b) ? (byte[])b.Clone() : null;
    }

    public string? ReadText(string name) => Read(name) is { } b ? Utf8.GetString(b) : null;

    /// <summary>Entry paths currently in the overlay (snapshot).</summary>
    public IReadOnlyList<string> EntryPaths
    {
        get { lock (_gate) return _entries.Keys.ToList(); }
    }

    public void Put(string name, byte[] bytes)
    {
        lock (_gate)
        {
            _entries[name] = (byte[])bytes.Clone();
            Flush();
        }
    }

    public void PutText(string name, string text) => Put(name, Utf8.GetBytes(text));

    /// <summary>Removes an entry. Returns true if it existed.</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            if (!_entries.Remove(name)) return false;
            Flush();
            return true;
        }
    }

    // Serialize all entries into a ZIP, encrypt, and atomically replace the file. Caller holds _gate.
    private void Flush()
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in _entries)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            zipBytes = ms.ToArray();
        }

        var encrypted = ContentCrypto.Encrypt(zipBytes);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        // Atomic replace: the previous overlay stays intact until the rename completes, so a crash
        // never leaves the instructor's deltas missing (the old file is only released by the move).
        File.Move(tmp, _path, overwrite: true);
    }
}
