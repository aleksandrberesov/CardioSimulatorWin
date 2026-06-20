using System;
using System.IO;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class FileTestSourceTests : IDisposable
{
    private readonly string _dir;

    public FileTestSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "test_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Write_Read_RoundTrips()
    {
        var src = new FileTestSource(_dir);
        var test = TestSeed.Sample(new[] { "ecg_a", "ecg_b", "ecg_c" });

        Assert.True(src.WriteTest(test));
        Assert.True(src.IsValid());

        var read = src.ReadTest(test.TestId);
        Assert.NotNull(read);
        Assert.Equal(test.Title, read!.Title);
        Assert.Equal(test.Questions.Count, read.Questions.Count);
    }

    [Fact]
    public void ReadTests_ReturnsAll_AndSkipsBadFiles()
    {
        var src = new FileTestSource(_dir);
        src.WriteTest(new Test("alpha", "Alpha", TestSeed.Sample(new[] { "x" }).Questions));
        src.WriteTest(new Test("beta", "Beta", TestSeed.Sample(new[] { "x" }).Questions));
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");

        var all = src.ReadTests();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.TestId == "alpha");
        Assert.Contains(all, t => t.TestId == "beta");
    }

    [Fact]
    public void Delete_RemovesTest()
    {
        var src = new FileTestSource(_dir);
        src.WriteTest(new Test("gamma", "Gamma", TestSeed.Sample(new[] { "x" }).Questions));
        Assert.NotNull(src.ReadTest("gamma"));

        Assert.True(src.DeleteTest("gamma"));
        Assert.Null(src.ReadTest("gamma"));
    }

    [Fact]
    public void UnsafeIds_AreRejected()
    {
        var src = new FileTestSource(_dir);
        Assert.False(src.WriteTest(new Test("../escape", "Bad", Array.Empty<TestQuestion>())));
        Assert.Null(src.ReadTest("../escape"));
        Assert.False(src.DeleteTest(".."));
    }

    [Fact]
    public void Repository_Caches_AndRaisesChangedOnWriteAndDelete()
    {
        var repo = new TestRepository(new FileTestSource(_dir));
        Assert.Empty(repo.Tests);

        var changed = 0;
        repo.Changed += (_, _) => changed++;

        Assert.True(repo.WriteTest(new Test("delta", "Delta", TestSeed.Sample(new[] { "x" }).Questions)));
        Assert.True(changed >= 1);
        Assert.Single(repo.Tests);
        Assert.Equal("Delta", repo.Test("delta")!.Title);

        Assert.True(repo.DeleteTest("delta"));
        Assert.Empty(repo.Tests);
    }
}
