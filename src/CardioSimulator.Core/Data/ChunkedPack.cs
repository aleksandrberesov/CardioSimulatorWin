using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Framing for the <c>CSP2</c> content pack: the plaintext is split into fixed-size chunks, each
/// encrypted independently with AES-256-GCM under its own nonce and tag. That is what makes a pack
/// randomly accessible — <see cref="ChunkedPackStream"/> decrypts only the chunks a read touches, so
/// the app never materialises the whole dataset. <c>CSP1</c> (one GCM message over the entire ZIP)
/// could only ever be decrypted whole, which cost ~2x the pack size at open, held the pack in RAM for
/// the process lifetime, and capped a pack at <see cref="Array.MaxLength"/> (~2 GB).
///
/// <para><b>Layout</b> (little-endian unless noted):
/// <code>
///   off  len  field
///     0    4  magic "CSP2"
///     4    1  version = 1
///     5    3  reserved (0)
///     8   16  salt          — fresh per pack; PBKDF2 input
///    24    8  nonceBase     — fresh per pack; high 8 bytes of every nonce
///    32    4  chunkSize     — plaintext bytes per chunk
///    36    8  plainLength   — total plaintext length
///    44    4  reserved (0)
///    48   16  headerTag     — GCM tag over an EMPTY plaintext, AAD = bytes[0..48]
///    64   ..  chunk 0: ciphertext(len0) ‖ tag(16)
///             chunk 1: ciphertext(len1) ‖ tag(16)  …
/// </code>
/// Every chunk but the last is exactly <c>chunkSize</c> plaintext bytes, so a chunk's file offset is
/// pure arithmetic (<see cref="FrameOffset"/>) — there is no index table to load at open.</para>
///
/// <para><b>Nonce discipline</b> (the load-bearing safety property): a nonce is
/// <c>nonceBase(8) ‖ big-endian uint32(chunkIndex)</c>. <paramref name="salt"/> and
/// <c>nonceBase</c> are both fresh-random per pack, so the derived key is unique per pack and within
/// that key each chunk index is used exactly once. Reusing a nonce under one key would collapse both
/// confidentiality and authenticity in GCM, so nothing here may ever be made deterministic.
/// <see cref="HeaderNonceIndex"/> is reserved for the header and can never collide with a chunk.</para>
///
/// <para><b>What the AEAD binds.</b> The header tag covers salt, nonceBase, chunkSize and
/// plainLength, so none can be edited to induce a mis-parse (CSP1 left them unauthenticated). Each
/// chunk's AAD is <c>index ‖ isFinal</c>: the index makes reordering fail, and <c>isFinal</c>
/// combined with the authenticated <c>plainLength</c> makes truncation unforgeable.</para>
/// </summary>
internal static class ChunkedPack
{
    /// <summary>Header size in bytes; also the file offset of chunk 0.</summary>
    internal const int HeaderLen = 64;

    /// <summary>Bytes the header authenticates (everything before the tag itself).</summary>
    internal const int HeaderAadLen = 48;

    internal const int TagLen = 16;
    internal const int SaltLen = 16;
    internal const int NonceBaseLen = 8;
    internal const int NonceLen = 12; // AesGcm.NonceByteSizes is min == max == 12 on .NET

    /// <summary>
    /// Plaintext bytes per chunk. 64 KiB keeps every buffer (chunk + tag = 65,552 B) under the 85,000-byte
    /// large-object-heap threshold, while being big enough that per-chunk GCM setup amortises.
    /// </summary>
    internal const int DefaultChunkSize = 65536;

    /// <summary>Reserved nonce index for the header, so it can never alias a chunk's nonce.</summary>
    internal const uint HeaderNonceIndex = 0xFFFFFFFF;

    /// <summary>Largest usable chunk index (<see cref="HeaderNonceIndex"/> is reserved).</summary>
    internal const uint MaxChunkIndex = 0xFFFFFFFE;

    internal static long FrameLen(int chunkSize) => (long)chunkSize + TagLen;

    /// <summary>File offset of chunk <paramref name="index"/>. Constant-time: every chunk but the
    /// last is full-size, so no lookup table is needed.</summary>
    internal static long FrameOffset(int index, int chunkSize) =>
        HeaderLen + index * FrameLen(chunkSize);

    /// <summary>Number of chunks holding <paramref name="plainLength"/> bytes (0 for an empty pack).</summary>
    internal static int ChunkCount(long plainLength, int chunkSize) =>
        (int)((plainLength + chunkSize - 1) / chunkSize);

    /// <summary>Plaintext length of chunk <paramref name="index"/> (the last one is short).</summary>
    internal static int ChunkLength(int index, long plainLength, int chunkSize)
    {
        var start = (long)index * chunkSize;
        var remaining = plainLength - start;
        return (int)Math.Min(chunkSize, remaining);
    }

    /// <summary>Exact file length a well-formed pack must have. Compared against the real length at
    /// open so a truncated or padded pack is rejected before any chunk is read.</summary>
    internal static long ExpectedFileLength(long plainLength, int chunkSize)
    {
        var chunks = ChunkCount(plainLength, chunkSize);
        if (chunks == 0) return HeaderLen;
        var lastLen = ChunkLength(chunks - 1, plainLength, chunkSize);
        return HeaderLen + (chunks - 1) * FrameLen(chunkSize) + lastLen + TagLen;
    }

    internal static void WriteNonce(Span<byte> nonce, ReadOnlySpan<byte> nonceBase, uint index)
    {
        nonceBase.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NonceBaseLen..], index);
    }

    /// <summary>Per-chunk associated data: <c>index ‖ isFinal</c>.</summary>
    internal static void WriteAad(Span<byte> aad, int index, bool isFinal)
    {
        BinaryPrimitives.WriteUInt32BigEndian(aad, (uint)index);
        aad[4] = isFinal ? (byte)1 : (byte)0;
    }

    internal const int AadLen = 5;
}

/// <summary>Random-access source of a pack's <i>ciphertext</i>. Abstracts "bytes on disk" from
/// "bytes already in memory" so the same decrypting stream serves both.</summary>
internal interface IPackBytes : IDisposable
{
    long Length { get; }
    /// <summary>Fills <paramref name="dest"/> from <paramref name="offset"/>, or throws.</summary>
    void Read(long offset, Span<byte> dest);
}

/// <summary>Ciphertext read from a file handle via <see cref="RandomAccess"/> — offset-based and
/// stateless, so it needs no seek lock and never allocates a whole-file buffer.</summary>
internal sealed class HandlePackBytes : IPackBytes
{
    private readonly SafeFileHandle _handle;

    public HandlePackBytes(SafeFileHandle handle)
    {
        _handle = handle;
        Length = RandomAccess.GetLength(handle);
    }

    public long Length { get; }

    public void Read(long offset, Span<byte> dest)
    {
        var read = 0;
        while (read < dest.Length)
        {
            var n = RandomAccess.Read(_handle, dest[read..], offset + read);
            if (n <= 0) throw new EndOfStreamException("Content pack ended early.");
            read += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Ciphertext already held in memory (small packs, overlays, tests).</summary>
internal sealed class MemoryPackBytes : IPackBytes
{
    private readonly ReadOnlyMemory<byte> _bytes;
    public MemoryPackBytes(ReadOnlyMemory<byte> bytes) => _bytes = bytes;
    public long Length => _bytes.Length;

    public void Read(long offset, Span<byte> dest)
    {
        if (offset + dest.Length > _bytes.Length) throw new EndOfStreamException("Content pack ended early.");
        _bytes.Span.Slice((int)offset, dest.Length).CopyTo(dest);
    }

    public void Dispose() { }
}

/// <summary>
/// A seekable, read-only <see cref="Stream"/> over a <c>CSP2</c> pack that decrypts chunks on demand.
/// This is the type that removes the whole-pack RAM cost: <see cref="ZipArchive"/> sits directly on
/// top of it and only ever pulls the central directory plus the entries actually read, so steady
/// memory is the ZIP index plus a small chunk cache rather than the entire dataset.
///
/// <para>Not thread-safe by itself; <see cref="EncryptedArchive"/> serialises access. Reads are
/// guarded internally anyway because the chunk cache is shared mutable state.</para>
/// </summary>
internal sealed class ChunkedPackStream : Stream
{
    private sealed class CacheSlot
    {
        public int Index = -1;
        public int Length;
        public long Stamp;
        public byte[] Plain = null!;
    }

    // 8 slots x 64 KiB = 512 KiB. ZipArchive scans the tail central directory then seeks per entry,
    // so a few slots stop the tail chunks being evicted by entry reads.
    private const int CacheSlots = 8;

    private readonly IPackBytes _src;
    private readonly byte[] _key;
    private readonly AesGcm _gcm;
    private readonly byte[] _nonceBase;
    private readonly int _chunkSize;
    private readonly long _plainLength;
    private readonly int _chunkCount;
    private readonly CacheSlot[] _cache;
    private readonly byte[] _frameBuf;
    private readonly object _gate = new();
    private long _position;
    private long _stamp;
    private bool _disposed;

    internal ChunkedPackStream(IPackBytes src, byte[] key, byte[] nonceBase, int chunkSize, long plainLength)
    {
        _src = src;
        _key = key;
        _gcm = new AesGcm(key, ChunkedPack.TagLen);
        _nonceBase = nonceBase;
        _chunkSize = chunkSize;
        _plainLength = plainLength;
        _chunkCount = ChunkedPack.ChunkCount(plainLength, chunkSize);
        _frameBuf = new byte[chunkSize + ChunkedPack.TagLen];
        _cache = new CacheSlot[CacheSlots];
        for (var i = 0; i < CacheSlots; i++) _cache[i] = new CacheSlot { Plain = new byte[chunkSize] };
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _plainLength;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new IOException("Cannot seek before the start of the pack.");
            _position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _plainLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        // Matches MemoryStream: seeking past the end is legal (reads then return 0); before it is not.
        if (target < 0) throw new IOException("Cannot seek before the start of the pack.");
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var total = 0;
            // Loop so a read spanning a chunk boundary is served from several chunks.
            while (!buffer.IsEmpty && _position < _plainLength)
            {
                var index = (int)(_position / _chunkSize);
                var inChunk = (int)(_position % _chunkSize);
                var slot = GetOrDecrypt(index);
                var available = slot.Length - inChunk;
                if (available <= 0) break;
                var n = Math.Min(buffer.Length, available);
                slot.Plain.AsSpan(inChunk, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }
    }

    private CacheSlot GetOrDecrypt(int index)
    {
        CacheSlot? lru = null;
        foreach (var slot in _cache)
        {
            if (slot.Index == index)
            {
                slot.Stamp = ++_stamp;
                return slot;
            }
            if (lru is null || slot.Stamp < lru.Stamp) lru = slot;
        }

        var victim = lru!;
        var len = ChunkedPack.ChunkLength(index, _plainLength, _chunkSize);
        var isFinal = index == _chunkCount - 1;
        _src.Read(ChunkedPack.FrameOffset(index, _chunkSize), _frameBuf.AsSpan(0, len + ChunkedPack.TagLen));

        Span<byte> nonce = stackalloc byte[ChunkedPack.NonceLen];
        ChunkedPack.WriteNonce(nonce, _nonceBase, (uint)index);
        Span<byte> aad = stackalloc byte[ChunkedPack.AadLen];
        ChunkedPack.WriteAad(aad, index, isFinal);

        // Invalidate first: if Decrypt throws (tampered chunk) the slot must not look valid. .NET
        // zeroes the destination before throwing, so no partial plaintext survives either.
        victim.Index = -1;
        _gcm.Decrypt(nonce, _frameBuf.AsSpan(0, len), _frameBuf.AsSpan(len, ChunkedPack.TagLen),
            victim.Plain.AsSpan(0, len), aad);
        victim.Index = index;
        victim.Length = len;
        victim.Stamp = ++_stamp;
        return victim;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Decrypted content must not linger in reusable buffers.
            foreach (var slot in _cache) CryptographicOperations.ZeroMemory(slot.Plain);
            CryptographicOperations.ZeroMemory(_frameBuf);
            CryptographicOperations.ZeroMemory(_key);
            _gcm.Dispose();
            _src.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Forward-only writing counterpart: frames and encrypts as bytes arrive, so building a pack costs a
/// couple of buffers instead of several copies of the whole dataset. <see cref="ZipArchive"/> in
/// Create mode writes straight through this.
///
/// <para>The destination must be seekable, because <see cref="Dispose"/> back-patches the header once
/// the final plaintext length is known.</para>
/// </summary>
internal sealed class ChunkedPackWriteStream : Stream
{
    private readonly Stream _dest;
    private readonly byte[] _key;
    private readonly AesGcm _gcm;
    private readonly byte[] _nonceBase;
    private readonly byte[] _salt;
    private readonly int _chunkSize;
    private readonly byte[] _staging;
    private readonly byte[] _frameBuf;
    private readonly long _headerOffset;
    private int _staged;
    private int _chunkIndex;
    private long _plainLength;
    private bool _disposed;

    internal ChunkedPackWriteStream(Stream dest, byte[] key, byte[] salt, byte[] nonceBase, int chunkSize)
    {
        if (!dest.CanSeek)
            throw new ArgumentException("A content pack destination must be seekable (the header is back-patched).", nameof(dest));
        _dest = dest;
        _key = key;
        _gcm = new AesGcm(key, ChunkedPack.TagLen);
        _salt = salt;
        _nonceBase = nonceBase;
        _chunkSize = chunkSize;
        _staging = new byte[chunkSize];
        _frameBuf = new byte[chunkSize + ChunkedPack.TagLen];
        _headerOffset = dest.Position;
        WriteHeader(plainLength: 0);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _plainLength;
    public override long Position { get => _plainLength; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            // Only flush a full staging buffer once more data is known to follow: the final chunk
            // must be tagged isFinal, and we only learn a chunk is final at Dispose.
            if (_staged == _chunkSize) FlushChunk(isFinal: false);
            var n = Math.Min(_chunkSize - _staged, buffer.Length);
            buffer[..n].CopyTo(_staging.AsSpan(_staged));
            _staged += n;
            _plainLength += n;
            buffer = buffer[n..];
        }
    }

    private void FlushChunk(bool isFinal)
    {
        // int.MaxValue, not MaxChunkIndex: the counter is an int, so it overflows long before a
        // chunk index could reach the reserved header nonce.
        if (_chunkIndex == int.MaxValue)
            throw new InvalidOperationException("Content pack exceeds the maximum chunk count.");

        Span<byte> nonce = stackalloc byte[ChunkedPack.NonceLen];
        ChunkedPack.WriteNonce(nonce, _nonceBase, (uint)_chunkIndex);
        Span<byte> aad = stackalloc byte[ChunkedPack.AadLen];
        ChunkedPack.WriteAad(aad, _chunkIndex, isFinal);

        _gcm.Encrypt(nonce, _staging.AsSpan(0, _staged), _frameBuf.AsSpan(0, _staged),
            _frameBuf.AsSpan(_staged, ChunkedPack.TagLen), aad);
        _dest.Write(_frameBuf.AsSpan(0, _staged + ChunkedPack.TagLen));
        _chunkIndex++;
        _staged = 0;
    }

    private void WriteHeader(long plainLength)
    {
        Span<byte> header = stackalloc byte[ChunkedPack.HeaderLen];
        header.Clear();
        ContentCrypto.MagicV2.CopyTo(header);
        header[4] = 1; // version
        _salt.CopyTo(header[8..]);
        _nonceBase.CopyTo(header[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint)_chunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[36..], plainLength);

        // Authenticate the header itself: an empty-plaintext GCM call whose AAD is the header bytes.
        // CSP1 left salt/nonce unauthenticated; here chunkSize and plainLength are bound too, so
        // neither can be edited to induce a mis-parse.
        Span<byte> nonce = stackalloc byte[ChunkedPack.NonceLen];
        ChunkedPack.WriteNonce(nonce, _nonceBase, ChunkedPack.HeaderNonceIndex);
        _gcm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty,
            header.Slice(ChunkedPack.HeaderAadLen, ChunkedPack.TagLen), header[..ChunkedPack.HeaderAadLen]);

        _dest.Write(header);
    }

    public override void Flush() => _dest.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                // Whatever is staged now is by construction the last chunk (see Write).
                if (_staged > 0) FlushChunk(isFinal: true);

                var end = _dest.Position;
                _dest.Position = _headerOffset;
                WriteHeader(_plainLength);
                _dest.Position = end;
                _dest.Flush();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(_staging);
                CryptographicOperations.ZeroMemory(_key);
                _gcm.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
