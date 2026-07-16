using System.Security.Cryptography;
using System.Text;
using CardioSimulator.Core.Data;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Conformance and tamper tests for the CSP2 chunked container. This is hand-written framing around
/// AES-GCM, so the failure modes are silent: a nonce or boundary bug produces bytes that still
/// "work" while being wrong or unauthenticated. These tests are the safety net for that.
/// </summary>
public class ChunkedPackTests
{
    private const int ChunkSize = 65536; // must match ChunkedPack.DefaultChunkSize

    private static byte[] Roundtrip(byte[] plaintext)
    {
        var pack = ContentCrypto.Encrypt(plaintext);
        return ContentCrypto.Decrypt(pack);
    }

    // ── framing conformance ─────────────────────────────────────────────

    [Theory]
    [InlineData(0)]                    // empty pack: zero chunks
    [InlineData(1)]
    [InlineData(ChunkSize - 1)]
    [InlineData(ChunkSize)]            // exactly one full chunk -> that chunk is final
    [InlineData(ChunkSize + 1)]
    [InlineData(ChunkSize * 2)]        // exact multiple: last full chunk must be marked final
    [InlineData(ChunkSize * 2 + 7)]
    [InlineData(ChunkSize * 3 + 12345)]
    public void RoundTripsAtEveryChunkBoundary(int length)
    {
        var data = new byte[length];
        RandomNumberGenerator.Fill(data);
        Assert.Equal(data, Roundtrip(data));
    }

    [Fact]
    public void PackIsCsp2AndPlaintextIsNotVisible()
    {
        var secret = Encoding.UTF8.GetBytes(new string('S', 5000) + "TOP_SECRET_MARKER");
        var pack = ContentCrypto.Encrypt(secret);

        Assert.Equal((byte)'C', pack[0]);
        Assert.Equal((byte)'S', pack[1]);
        Assert.Equal((byte)'P', pack[2]);
        Assert.Equal((byte)'2', pack[3]);
        Assert.True(ContentCrypto.LooksLikePack(pack));

        // The marker must not appear anywhere in the ciphertext.
        var haystack = Encoding.Latin1.GetString(pack);
        Assert.DoesNotContain("TOP_SECRET_MARKER", haystack, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPackDiffersEvenForIdenticalInput()
    {
        // Fresh salt + nonce base per pack is what guarantees a nonce is never reused under one key.
        var data = Encoding.UTF8.GetBytes("identical input");
        var a = ContentCrypto.Encrypt(data);
        var b = ContentCrypto.Encrypt(data);
        Assert.NotEqual(a, b);
        Assert.Equal(data, ContentCrypto.Decrypt(a));
        Assert.Equal(data, ContentCrypto.Decrypt(b));
    }

    [Fact]
    public void NoncesAreUniqueAcrossChunks()
    {
        // Directly assert the property the whole construction rests on: within one pack, no two
        // chunks (nor the header) share a nonce.
        var nonceBase = RandomNumberGenerator.GetBytes(ChunkedPack.NonceBaseLen);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nonce = new byte[ChunkedPack.NonceLen];

        foreach (var index in new uint[] { 0, 1, 2, 255, 256, 65535, 65536, 26750, ChunkedPack.MaxChunkIndex })
        {
            ChunkedPack.WriteNonce(nonce, nonceBase, index);
            Assert.True(seen.Add(Convert.ToHexString(nonce)), $"nonce reused for chunk {index}");
        }
        ChunkedPack.WriteNonce(nonce, nonceBase, ChunkedPack.HeaderNonceIndex);
        Assert.True(seen.Add(Convert.ToHexString(nonce)), "header nonce collided with a chunk nonce");
        Assert.True(ChunkedPack.HeaderNonceIndex > ChunkedPack.MaxChunkIndex);
    }

    [Fact]
    public void FrameOffsetsAndLengthsAreSelfConsistent()
    {
        const long plain = ChunkSize * 3L + 100;
        var chunks = ChunkedPack.ChunkCount(plain, ChunkSize);
        Assert.Equal(4, chunks);
        Assert.Equal(ChunkSize, ChunkedPack.ChunkLength(0, plain, ChunkSize));
        Assert.Equal(100, ChunkedPack.ChunkLength(3, plain, ChunkSize));

        // The last frame must land exactly at the end of the file.
        var last = ChunkedPack.FrameOffset(3, ChunkSize) + 100 + ChunkedPack.TagLen;
        Assert.Equal(ChunkedPack.ExpectedFileLength(plain, ChunkSize), last);
        Assert.Equal(ChunkedPack.HeaderLen, ChunkedPack.ExpectedFileLength(0, ChunkSize));
    }

    // ── tamper matrix ───────────────────────────────────────────────────

    [Fact]
    public void TamperedFinalTagIsRejected()
    {
        var pack = ContentCrypto.Encrypt(Encoding.UTF8.GetBytes("payload"));
        pack[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(pack));
    }

    [Theory]
    [InlineData(8)]    // salt
    [InlineData(24)]   // nonce base
    [InlineData(32)]   // chunkSize
    [InlineData(36)]   // plainLength
    [InlineData(48)]   // header tag
    public void TamperedHeaderFieldIsRejectedAtOpen(int offset)
    {
        // CSP1 left salt/nonce unauthenticated. CSP2 binds the whole header, so editing any of these
        // must fail immediately rather than causing a mis-parse.
        var pack = ContentCrypto.Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 2));
        pack[offset] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(pack));
    }

    [Fact]
    public void TamperedMiddleChunkIsRejected()
    {
        var pack = ContentCrypto.Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 3));
        pack[ChunkedPack.HeaderLen + ChunkSize + 100] ^= 0xFF; // inside chunk 1
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(pack));
    }

    [Fact]
    public void SwappedChunksAreRejected()
    {
        // Reordering must fail: the chunk index is bound into both the nonce and the AAD.
        var pack = ContentCrypto.Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 3));
        var frame = ChunkSize + ChunkedPack.TagLen;
        var a = ChunkedPack.HeaderLen;
        var b = ChunkedPack.HeaderLen + frame;
        var tmp = pack[a..(a + frame)];
        Array.Copy(pack, b, pack, a, frame);
        tmp.CopyTo(pack, b);
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(pack));
    }

    [Fact]
    public void TruncatedPackIsRejected()
    {
        var pack = ContentCrypto.Encrypt(RandomNumberGenerator.GetBytes(ChunkSize * 3));
        var cut = pack[..(pack.Length - (ChunkSize + ChunkedPack.TagLen))];
        // plainLength is authenticated in the header, so dropping a whole frame is detectable.
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(cut));
    }

    [Fact]
    public void PaddedPackIsRejected()
    {
        var pack = ContentCrypto.Encrypt(RandomNumberGenerator.GetBytes(1000));
        var padded = pack.Concat(new byte[16]).ToArray();
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(padded));
    }

    [Fact]
    public void NonPackBytesAreRejected()
    {
        Assert.False(ContentCrypto.LooksLikePack(Encoding.UTF8.GetBytes("PKnot a pack")));
        Assert.ThrowsAny<CryptographicException>(
            () => ContentCrypto.Decrypt(Encoding.UTF8.GetBytes("PKnot a pack at all")));
    }

    // ── seekable read stream conformance ────────────────────────────────

    [Fact]
    public void StreamMatchesMemoryStreamAtEveryOffsetAndLength()
    {
        // The read path must behave exactly like the MemoryStream it replaces, including reads that
        // straddle chunk boundaries — that is what ZipArchive depends on.
        var plain = new byte[ChunkSize * 2 + 4321];
        RandomNumberGenerator.Fill(plain);
        var pack = ContentCrypto.Encrypt(plain);

        using var stream = ContentCrypto.OpenDecryptingRead(pack);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(plain.Length, stream.Length);

        var rng = new Random(12345);
        var offsets = new List<int> { 0, 1, ChunkSize - 1, ChunkSize, ChunkSize + 1, ChunkSize * 2 - 1, ChunkSize * 2, plain.Length - 1 };
        for (var i = 0; i < 60; i++) offsets.Add(rng.Next(plain.Length));

        foreach (var offset in offsets)
        {
            foreach (var len in new[] { 1, 7, 4096, ChunkSize - 1, ChunkSize, ChunkSize + 33 })
            {
                stream.Position = offset;
                var expectedLen = Math.Min(len, plain.Length - offset);
                var buf = new byte[len];
                var got = stream.Read(buf, 0, len);
                Assert.Equal(expectedLen, got);
                Assert.Equal(plain.AsSpan(offset, expectedLen).ToArray(), buf.AsSpan(0, got).ToArray());
            }
        }
    }

    [Fact]
    public void StreamSeekSemanticsMatchMemoryStream()
    {
        var plain = RandomNumberGenerator.GetBytes(5000);
        var pack = ContentCrypto.Encrypt(plain);
        using var stream = ContentCrypto.OpenDecryptingRead(pack);

        Assert.Equal(100, stream.Seek(100, SeekOrigin.Begin));
        Assert.Equal(150, stream.Seek(50, SeekOrigin.Current));
        Assert.Equal(5000, stream.Seek(0, SeekOrigin.End));

        // Past the end is legal and reads return 0 (MemoryStream behaviour ZipArchive relies on).
        stream.Position = plain.Length + 500;
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));

        // Before the start is not.
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void ReadsSurviveCacheEviction()
    {
        // More chunks than cache slots, walked backwards to force eviction and re-decryption.
        var plain = new byte[ChunkSize * 20];
        RandomNumberGenerator.Fill(plain);
        var pack = ContentCrypto.Encrypt(plain);
        using var stream = ContentCrypto.OpenDecryptingRead(pack);

        for (var chunk = 19; chunk >= 0; chunk--)
        {
            stream.Position = (long)chunk * ChunkSize;
            var buf = new byte[64];
            stream.ReadExactly(buf);
            Assert.Equal(plain.AsSpan(chunk * ChunkSize, 64).ToArray(), buf);
        }
    }
}
