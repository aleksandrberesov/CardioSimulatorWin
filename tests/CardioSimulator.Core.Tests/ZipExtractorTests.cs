using System.IO.Compression;
using CardioSimulator.Core.Data;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class ZipExtractorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _zip;
    private readonly string _target;

    public ZipExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardio_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _zip = Path.Combine(_dir, "data.zip");
        _target = Path.Combine(_dir, "out");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void MakeZip(int entries)
    {
        using var fs = File.Create(_zip);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        for (var i = 0; i < entries; i++)
        {
            var entry = archive.CreateEntry($"file{i:D4}.dat");
            using var w = new StreamWriter(entry.Open());
            w.Write($"payload {i}");
        }
    }

    [Fact]
    public void Extract_UnpacksAllEntries_AndReportsFinalProgress()
    {
        MakeZip(10);
        ZipProgress last = default;
        var progress = new TestProgress<ZipProgress>(p => last = p);

        var ok = ZipExtractor.Extract(_zip, _target, progress);

        Assert.True(ok);
        Assert.Equal(10, Directory.GetFiles(_target).Length);
        Assert.Equal(10, last.Done);
        Assert.Equal(10, last.Total);
    }

    [Fact]
    public void Extract_AlreadyCancelledToken_ThrowsAndCleansUp()
    {
        MakeZip(10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ZipExtractor.Extract(_zip, _target, progress: null, cancellationToken: cts.Token));

        // The (never-populated) target directory must not be left behind for the caller to load.
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void Extract_CancelledMidway_ThrowsAndRemovesPartialOutput()
    {
        MakeZip(50);
        using var cts = new CancellationTokenSource();
        // Cancel once the first progress tick arrives, i.e. partway through the archive.
        var progress = new TestProgress<ZipProgress>(_ => cts.Cancel());

        Assert.Throws<OperationCanceledException>(
            () => ZipExtractor.Extract(_zip, _target, progress, cts.Token));

        Assert.False(Directory.Exists(_target));
    }

    private sealed class TestProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public TestProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}
