using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CardioSimulator.Core.Data;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class ContentCryptoTests
{
    [Fact]
    public void Encrypt_then_Decrypt_round_trips()
    {
        var plain = Encoding.UTF8.GetBytes("manifest:1\n1abblock;1 AB block\n");
        var pack = ContentCrypto.Encrypt(plain);

        Assert.True(ContentCrypto.LooksLikePack(pack));
        Assert.NotEqual(plain, pack[..plain.Length]); // not stored in the clear
        Assert.Equal(plain, ContentCrypto.Decrypt(pack));
    }

    [Fact]
    public void Encrypt_uses_fresh_nonce_so_output_differs_each_time()
    {
        var plain = Encoding.UTF8.GetBytes("same input");
        var a = ContentCrypto.Encrypt(plain);
        var b = ContentCrypto.Encrypt(plain);

        Assert.NotEqual(a, b);                       // different salt/nonce
        Assert.Equal(plain, ContentCrypto.Decrypt(a));
        Assert.Equal(plain, ContentCrypto.Decrypt(b));
    }

    [Fact]
    public void Decrypt_rejects_tampered_ciphertext()
    {
        var pack = ContentCrypto.Encrypt(Encoding.UTF8.GetBytes("protected"));
        pack[^1] ^= 0xFF; // flip a ciphertext byte → GCM tag must fail

        // AesGcm throws AuthenticationTagMismatchException, a CryptographicException subtype.
        Assert.ThrowsAny<CryptographicException>(() => ContentCrypto.Decrypt(pack));
    }

    [Fact]
    public void Decrypt_rejects_non_pack_bytes()
    {
        Assert.False(ContentCrypto.LooksLikePack(Encoding.UTF8.GetBytes("PK\x03\x04 plain zip")));
        Assert.ThrowsAny<CryptographicException>(
            () => ContentCrypto.Decrypt(Encoding.UTF8.GetBytes("not a pack at all")));
    }

    [Fact]
    public void EncryptedArchive_reads_entries_by_name_and_path()
    {
        // Build a tiny ZIP in memory, encrypt it, then read it back through the runtime path.
        var zipBytes = BuildZip(
            ("manifest.txt", "manifest:1\n"),
            ("sinus.dat", "id:sinus\n"),
            ("cardio-101/course.txt", "course:cardio-101\n"));
        var pack = ContentCrypto.Encrypt(zipBytes);

        using var archive = EncryptedArchive.OpenBytes(pack);

        Assert.Equal("manifest:1\n", archive.ReadByNameText("manifest.txt"));
        Assert.Equal("id:sinus\n", archive.ReadByNameText("sinus.dat"));
        Assert.Equal("course:cardio-101\n", archive.ReadPathText("cardio-101/course.txt"));
        Assert.Contains("sinus.dat", archive.FileNamesWithExtension(".dat"));
        Assert.Null(archive.ReadByNameText("missing.dat"));
    }

    [Fact]
    public void PackIdentity_is_unique_per_pack_and_stable_per_file()
    {
        // The overlay layer keys off this: two packs (even of identical content) must never share an
        // identity, or a re-exported pack would inherit the previous pack's edits/tombstones — the
        // "loads empty, only structure" bug. Re-reading one file must be stable.
        var data = Encoding.UTF8.GetBytes("identical dataset");
        var dir = Path.Combine(Path.GetTempPath(), "cs_ident_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.pak");
            var b = Path.Combine(dir, "b.pak");
            File.WriteAllBytes(a, ContentCrypto.Encrypt(data)); // fresh salt/nonce
            File.WriteAllBytes(b, ContentCrypto.Encrypt(data)); // fresh salt/nonce (different pack)

            Assert.True(ContentCrypto.TryReadPackIdentity(a, out var idA));
            Assert.True(ContentCrypto.TryReadPackIdentity(b, out var idB));
            Assert.True(ContentCrypto.TryReadPackIdentity(a, out var idA2)); // re-read same file

            Assert.NotEqual(idA, idB);   // two packs → two identities
            Assert.Equal(idA, idA2);     // same file → stable identity
            Assert.NotEmpty(idA);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PackIdentity_returns_false_for_missing_or_non_pack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs_ident_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(ContentCrypto.TryReadPackIdentity(Path.Combine(dir, "nope.pak"), out _));
            var junk = Path.Combine(dir, "junk.pak");
            File.WriteAllBytes(junk, Encoding.UTF8.GetBytes("PKnot a pack at all here"));
            Assert.False(ContentCrypto.TryReadPackIdentity(junk, out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static byte[] BuildZip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }
}
