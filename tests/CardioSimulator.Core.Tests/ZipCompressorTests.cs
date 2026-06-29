using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CardioSimulator.Core.Data;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class ZipCompressorTests : IDisposable
{
    private readonly string _tempDir;

    public ZipCompressorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ZipCompressorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Zip_RecursiveDirectory_PreservesHierarchy()
    {
        // Arrange
        var sourceDir = Path.Combine(_tempDir, "Source");
        var destZip = Path.Combine(_tempDir, "output.zip");

        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "subdir"));

        File.WriteAllText(Path.Combine(sourceDir, "manifest.txt"), "manifest content");
        File.WriteAllText(Path.Combine(sourceDir, "subdir", "lecture.html"), "lecture content");

        // Act
        var success = ZipCompressor.Zip(sourceDir, destZip);

        // Assert
        Assert.True(success);
        Assert.True(File.Exists(destZip));

        using var archive = ZipFile.OpenRead(destZip);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains("manifest.txt", entries);
        Assert.Contains("subdir/lecture.html", entries);
    }
}
