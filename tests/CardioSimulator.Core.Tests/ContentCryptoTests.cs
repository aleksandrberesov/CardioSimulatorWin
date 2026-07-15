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
