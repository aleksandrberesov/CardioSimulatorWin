using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class OskeResultStoreTests : IDisposable
{
    private readonly string _dir;

    public OskeResultStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oske_results_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static OskeResult Result(string fullName, DateTimeOffset ts)
    {
        var form = OskeSeedForms.Therapy();
        var map = form.Questions.ToDictionary(q => q.Id, q => (IReadOnlyList<string>)new[] { q.Options[0].Id });
        return OskeGrader.Grade(form, new OskeAnswerKey("ecg1", form.FormId, map), map,
            new OskeStudentInfo(fullName, "Группа-1"), "ecg1", ts);
    }

    [Fact]
    public void Save_ThenList_ReturnsResultsNewestFirst()
    {
        var store = new OskeResultStore(_dir);
        Assert.True(store.Save(Result("Иванов Иван", new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero))));
        Assert.True(store.Save(Result("Петров Пётр", new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero))));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("Петров Пётр", list[0].Student.FullName); // newest first
        Assert.Equal("Иванов Иван", list[1].Student.FullName);
    }

    [Fact]
    public void Save_TwoAttemptsSameSecond_DoesNotOverwrite()
    {
        var store = new OskeResultStore(_dir);
        var ts = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        Assert.True(store.Save(Result("Иванов Иван", ts)));
        Assert.True(store.Save(Result("Иванов Иван", ts)));

        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void List_MissingDirectory_ReturnsEmpty()
    {
        var store = new OskeResultStore(_dir);
        Assert.Empty(store.List());
    }
}
