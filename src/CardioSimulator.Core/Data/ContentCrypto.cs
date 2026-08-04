using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Symmetric encryption for the bundled content packs (<c>*.pak</c>) — AES-256-GCM with a
/// PBKDF2-derived key. The same code is used by the offline packer tool (to produce a
/// <c>.pak</c>) and by the runtime (<see cref="EncryptedArchive"/>) to read it, so the two can never
/// drift apart.
///
/// <para><b>Threat model.</b> This protects the vendor dataset against <i>casual</i> copying —
/// a student cannot open the pack in an archiver or drag loose files out of the app-data folder.
/// It is <i>not</i> unbreakable DRM: the app must decrypt to render, so the key material ships
/// inside the binary. <see cref="Secret"/> is assembled from split, XOR-masked bytes rather than a
/// plaintext literal to raise the cost of pulling it out, but a determined reverse-engineer can
/// still recover it. Pair with a binary obfuscator if you need to raise that bar further.</para>
///
/// <para><b>Two container versions exist.</b>
/// <c>CSP1</c> — <c>[ magic "CSP1" (4) ][ salt (16) ][ nonce (12) ][ tag (16) ][ ciphertext (n) ]</c>
/// — is one GCM message over the whole ZIP. It can only be decrypted in one shot, which cost ~2x the
/// pack size at open, pinned the pack in RAM for the process lifetime, and capped a pack at
/// <see cref="Array.MaxLength"/>. <c>CSP2</c> (see <see cref="ChunkedPack"/>) frames the plaintext
/// into independently encrypted chunks so a pack can be read lazily off disk in constant memory.
/// New packs are written as CSP2; CSP1 packs already in the field keep loading unchanged, forever.</para>
///
/// <para>In both versions the salt (and, in CSP2, the nonce base) is fresh-random per pack, so
/// re-packing identical input yields different bytes and a nonce is never reused under a derived key.</para>
/// </summary>
public static class ContentCrypto
{
    private static readonly byte[] Magic = "CSP1"u8.ToArray();

    /// <summary>CSP2 magic. Exposed to <see cref="ChunkedPackWriteStream"/>, which stamps the header.</summary>
    internal static ReadOnlySpan<byte> MagicV2 => "CSP2"u8;

    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32; // AES-256
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>CSP1 header size that precedes the ciphertext.</summary>
    private const int HeaderLen = 4 + SaltLen + NonceLen + TagLen;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into a self-describing pack blob, in the current
    /// (<c>CSP2</c>) format. Still materialises the whole result, so it is only for small payloads —
    /// overlays and tests. Build a distributable pack with <see cref="CreateEncryptingWrite"/>, which
    /// streams and therefore has no size ceiling.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using var ms = new MemoryStream();
        using (var stream = CreateEncryptingWrite(ms, leaveOpen: true))
        {
            stream.Write(plaintext);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Decrypts a whole pack blob into a new array, accepting either container version. Throws
    /// <see cref="CryptographicException"/> if the header is malformed or an authentication tag does
    /// not verify (wrong key or tampered data).
    ///
    /// <para>This materialises the entire plaintext, so it suits overlays and tests but not the
    /// dataset. <see cref="EncryptedArchive"/> reads CSP2 packs lazily instead.</para>
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> pack)
    {
        if (LooksLikeV2(pack)) return DecryptV2(pack);

        if (pack.Length < HeaderLen || !pack[..4].SequenceEqual(Magic))
            throw new CryptographicException("Not a valid content pack.");

        var pos = 4;
        var salt = pack.Slice(pos, SaltLen); pos += SaltLen;
        var nonce = pack.Slice(pos, NonceLen); pos += NonceLen;
        var tag = pack.Slice(pos, TagLen); pos += TagLen;
        var cipher = pack[pos..];

        var key = DeriveKey(salt);
        try
        {
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecryptV2(ReadOnlySpan<byte> pack)
    {
        // Reuse the lazy reader so there is exactly one CSP2 parser: a second hand-written chunk loop
        // here would be free to drift from the one that matters.
        var copy = pack.ToArray();
        using var stream = OpenDecryptingRead(copy);
        if (stream.Length > Array.MaxLength)
            throw new CryptographicException("Content pack is too large to decrypt into a single array.");
        var plain = new byte[stream.Length];
        stream.ReadExactly(plain);
        return plain;
    }

    /// <summary>True if <paramref name="data"/> begins with either pack magic. Both versions must be
    /// accepted here: this is what stops a saved CSP1 pick being treated as "not a pack".</summary>
    public static bool LooksLikePack(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && (data[..4].SequenceEqual(Magic) || data[..4].SequenceEqual(MagicV2));

    /// <summary>True only for the chunked (<c>CSP2</c>) container, which can be read lazily.</summary>
    internal static bool LooksLikeV2(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[..4].SequenceEqual(MagicV2);

    /// <summary>
    /// Reads a stable, per-pack-unique identity from the header of the pack at <paramref name="path"/>
    /// — the random salt (plus, for CSP2, the nonce base) that every pack draws fresh at creation.
    ///
    /// <para>Two distinct pack files never share an identity: even re-packing the <i>same</i> dataset
    /// produces a new salt, so the identity changes. Re-reading one unchanged file always yields the
    /// same bytes. That makes it the right key for binding per-pack state — notably the Full-edition
    /// writable overlay — to the exact pack instance, so a pack replaced at a path already in use can
    /// never inherit the previous pack's edits and tombstones.</para>
    ///
    /// <para>Returns false (and an empty identity) if the file is missing, too short, or not a pack.</para>
    /// </summary>
    public static bool TryReadPackIdentity(string path, out byte[] identity)
    {
        // salt(16) [+ nonce/nonceBase] begins at offset 4 (CSP1) or 8 (CSP2); 32 bytes covers both.
        const int V1IdOffset = 4, V1IdLen = SaltLen + NonceLen;                      // 4 → 28 bytes
        const int V2IdOffset = 8, V2IdLen = ChunkedPack.SaltLen + ChunkedPack.NonceBaseLen; // 8 → 24 bytes

        identity = Array.Empty<byte>();
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[32];
            var read = 0;
            while (read < head.Length)
            {
                var n = fs.Read(head[read..]);
                if (n <= 0) break;
                read += n;
            }
            if (read < head.Length || !LooksLikePack(head)) return false;

            identity = LooksLikeV2(head)
                ? head.Slice(V2IdOffset, V2IdLen).ToArray()
                : head.Slice(V1IdOffset, V1IdLen).ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── CSP2: lazy read / streaming write ───────────────────────────────

    /// <summary>
    /// Opens a seekable decrypting view over the CSP2 pack behind <paramref name="handle"/>. The
    /// header is authenticated before any chunk is read, and the file length is checked against the
    /// header's, so truncation and padding are rejected up front. Takes ownership of the handle.
    /// </summary>
    /// <exception cref="CryptographicException">Not a CSP2 pack, or the header fails to authenticate.</exception>
    internal static Stream OpenDecryptingRead(SafeFileHandle handle)
    {
        IPackBytes src = new HandlePackBytes(handle);
        try { return OpenDecryptingRead(src); }
        catch { src.Dispose(); throw; }
    }

    /// <summary>In-memory counterpart of <see cref="OpenDecryptingRead(SafeFileHandle)"/>.</summary>
    internal static Stream OpenDecryptingRead(ReadOnlyMemory<byte> pack) =>
        OpenDecryptingRead(new MemoryPackBytes(pack));

    private static Stream OpenDecryptingRead(IPackBytes src)
    {
        if (src.Length < ChunkedPack.HeaderLen)
            throw new CryptographicException("Not a valid content pack.");

        Span<byte> header = stackalloc byte[ChunkedPack.HeaderLen];
        src.Read(0, header);
        if (!LooksLikeV2(header))
            throw new CryptographicException("Not a chunked (CSP2) content pack.");
        if (header[4] != 1)
            throw new CryptographicException($"Unsupported content pack version {header[4]}.");

        var salt = header.Slice(8, ChunkedPack.SaltLen);
        var nonceBase = header.Slice(24, ChunkedPack.NonceBaseLen).ToArray();
        var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        var plainLength = BinaryPrimitives.ReadInt64LittleEndian(header[36..]);

        // Sanity-check before trusting the values for arithmetic. These are authenticated below, so
        // this only guards against a header that is malformed rather than forged.
        if (chunkSize <= 0 || plainLength < 0)
            throw new CryptographicException("Content pack header is malformed.");

        var key = DeriveKey(salt);
        var ok = false;
        try
        {
            using (var gcm = new AesGcm(key, TagLen))
            {
                Span<byte> nonce = stackalloc byte[ChunkedPack.NonceLen];
                ChunkedPack.WriteNonce(nonce, nonceBase, ChunkedPack.HeaderNonceIndex);
                // Authenticates salt, nonceBase, chunkSize and plainLength together.
                gcm.Decrypt(nonce, ReadOnlySpan<byte>.Empty,
                    header.Slice(ChunkedPack.HeaderAadLen, TagLen), Span<byte>.Empty,
                    header[..ChunkedPack.HeaderAadLen]);
            }

            if (src.Length != ChunkedPack.ExpectedFileLength(plainLength, chunkSize))
                throw new CryptographicException("Content pack is truncated or padded.");

            var stream = new ChunkedPackStream(src, key, nonceBase, chunkSize, plainLength);
            ok = true; // ChunkedPackStream now owns key + src and zeroes/disposes them.
            return stream;
        }
        finally
        {
            if (!ok) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Wraps <paramref name="destination"/> in a stream that frames and encrypts as bytes arrive,
    /// producing a CSP2 pack in constant memory. <paramref name="destination"/> must be seekable
    /// (the header is back-patched on dispose once the plaintext length is known).
    ///
    /// <para>Public so the offline packer builds packs through the same code the runtime reads —
    /// a second implementation would be free to drift.</para>
    /// </summary>
    public static Stream CreateEncryptingWrite(Stream destination, bool leaveOpen = false)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonceBase = RandomNumberGenerator.GetBytes(ChunkedPack.NonceBaseLen);
        var key = DeriveKey(salt);
        var inner = new ChunkedPackWriteStream(destination, key, salt, nonceBase, ChunkedPack.DefaultChunkSize);
        return leaveOpen ? inner : new OwningWriteStream(inner, destination);
    }

    /// <summary>Disposes the pack stream and then the file underneath it, in that order — the header
    /// back-patch must land before the destination closes.</summary>
    private sealed class OwningWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly Stream _dest;
        public OwningWriteStream(Stream inner, Stream dest) { _inner = inner; _dest = dest; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _dest.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> salt)
    {
        var secret = Secret();
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secret, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The app secret, reassembled at call time from two split, XOR-masked halves so it never
    /// appears as a single constant in the binary. Changing these bytes invalidates every existing
    /// <c>.pak</c> — re-run the packer if you rotate the secret.
    /// </summary>
    private static byte[] Secret()
    {
        // Two independent byte streams; the secret is a[i] ^ b[i] ^ Mask. Neither array alone is
        // the key, and the plaintext key is materialised only transiently in DeriveKey.
        ReadOnlySpan<byte> a = new byte[]
        {
            0x9E, 0x14, 0xC3, 0x57, 0x2A, 0xBB, 0x60, 0xD9,
            0x0F, 0x88, 0x41, 0xE2, 0x7C, 0x35, 0xA6, 0x1D,
            0xF4, 0x69, 0x02, 0x9B, 0xCE, 0x50, 0xB7, 0x28,
            0x83, 0x3E, 0xD1, 0x46, 0xAA, 0x11, 0x6F, 0xDC,
        };
        ReadOnlySpan<byte> b = new byte[]
        {
            0x51, 0xC7, 0x3A, 0x08, 0x9D, 0x26, 0xE4, 0x72,
            0xB0, 0x1F, 0x66, 0xCB, 0x39, 0x84, 0x5D, 0xF1,
            0x0A, 0x93, 0x48, 0x2C, 0x75, 0xBE, 0x17, 0xD0,
            0x6B, 0xE9, 0x54, 0xA2, 0x3F, 0x8C, 0x21, 0x40,
        };
        const byte Mask = 0x5A;
        var key = new byte[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            key[i] = (byte)(a[i] ^ b[i] ^ Mask);
        }
        return key;
    }
}
