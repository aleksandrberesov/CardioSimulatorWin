using System.Security.Cryptography;
using System.Text;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Symmetric encryption for the bundled content packs (<c>*.pak</c>) — AES-256-GCM with a
/// PBKDF2-derived key. The same code is used by the offline packer tool (to produce a
/// <c>.pak</c> from a plain ZIP) and by the runtime (<see cref="EncryptedArchive"/>) to decrypt
/// it into memory, so the two can never drift apart.
///
/// <para><b>Threat model.</b> This protects the vendor dataset against <i>casual</i> copying —
/// a student cannot open the pack in an archiver or drag loose files out of the app-data folder.
/// It is <i>not</i> unbreakable DRM: the app must decrypt to render, so the key material ships
/// inside the binary. <see cref="Secret"/> is assembled from split, XOR-masked bytes rather than a
/// plaintext literal to raise the cost of pulling it out, but a determined reverse-engineer can
/// still recover it. Pair with a binary obfuscator if you need to raise that bar further.</para>
///
/// <para>Container layout (all lengths in bytes):
/// <c>[ magic "CSP1" (4) ][ salt (16) ][ nonce (12) ][ tag (16) ][ ciphertext (n) ]</c>.
/// The salt and nonce are fresh-random per pack, so re-packing identical input yields different
/// bytes and a nonce is never reused under the derived key.</para>
/// </summary>
public static class ContentCrypto
{
    private static readonly byte[] Magic = "CSP1"u8.ToArray();
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32; // AES-256
    private const int Pbkdf2Iterations = 100_000;

    /// <summary>Header size that precedes the ciphertext.</summary>
    private const int HeaderLen = 4 + SaltLen + NonceLen + TagLen;

    /// <summary>Encrypts <paramref name="plaintext"/> into a self-describing pack blob.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = DeriveKey(salt);
        try
        {
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagLen];
            using (var gcm = new AesGcm(key, TagLen))
            {
                gcm.Encrypt(nonce, plaintext, cipher, tag);
            }

            var output = new byte[HeaderLen + cipher.Length];
            var pos = 0;
            Magic.CopyTo(output.AsSpan(pos)); pos += Magic.Length;
            salt.CopyTo(output.AsSpan(pos)); pos += SaltLen;
            nonce.CopyTo(output.AsSpan(pos)); pos += NonceLen;
            tag.CopyTo(output.AsSpan(pos)); pos += TagLen;
            cipher.CopyTo(output.AsSpan(pos));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts a pack blob produced by <see cref="Encrypt"/>. Throws
    /// <see cref="CryptographicException"/> if the header is malformed or the authentication tag
    /// does not verify (wrong key or tampered data).
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> pack)
    {
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

    /// <summary>True if <paramref name="data"/> begins with the pack magic header.</summary>
    public static bool LooksLikePack(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[..4].SequenceEqual(Magic);

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
