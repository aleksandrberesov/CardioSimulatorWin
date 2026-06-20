using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Caches the authored tests and brokers reads/writes. Raises <see cref="Changed"/> when the source
/// swaps or a test is written/deleted (the Testing screen + constructor listen, mirroring
/// <see cref="OskeRepository"/> / <see cref="CourseRepository.ManifestChanged"/>).
/// </summary>
public class TestRepository
{
    private ITestSource _source;
    private IReadOnlyList<Test>? _tests;

    public TestRepository(ITestSource source)
    {
        _source = source;
    }

    public event EventHandler? Changed;

    public void SetSource(ITestSource newSource)
    {
        _source = newSource;
        _tests = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The available tests (cached). Empty when none are on disk.</summary>
    public IReadOnlyList<Test> Tests => _tests ??= _source.ReadTests();

    public Test? Test(string testId) => Tests.FirstOrDefault(t => t.TestId == testId);

    public bool WriteTest(Test test)
    {
        if (_source is not FileTestSource fs) return false;
        var ok = fs.WriteTest(test);
        if (ok)
        {
            _tests = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    public bool DeleteTest(string testId)
    {
        if (_source is not FileTestSource fs) return false;
        var ok = fs.DeleteTest(testId);
        if (ok)
        {
            _tests = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    /// <summary>Forces a re-read of the cached tests on next access and notifies listeners.</summary>
    public void Reload()
    {
        _tests = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
