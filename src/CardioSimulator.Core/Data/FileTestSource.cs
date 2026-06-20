using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// File-backed source for self-assessment tests. Layout under <see cref="Root"/>:
/// <code>
/// &lt;testId&gt;.json   – one test per file
/// </code>
/// All writes are atomic (temp file + move), mirroring <see cref="FileOskeSource"/> /
/// <see cref="FileCourseSource"/>.
/// </summary>
public class FileTestSource : ITestSource
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public FileTestSource(string root)
    {
        Root = root;
    }

    /// <summary>Rejects ids that could escape the folder (path traversal / separators).</summary>
    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        id is not "." and not "..";

    public IReadOnlyList<Test> ReadTests()
    {
        if (!Directory.Exists(Root)) return Array.Empty<Test>();
        var tests = new List<Test>();
        foreach (var path in Directory.GetFiles(Root, "*.json"))
        {
            try
            {
                if (TestJson.Deserialize(File.ReadAllText(path, Encoding.UTF8)) is { } test)
                    tests.Add(test);
            }
            catch
            {
                // skip an unreadable test file
            }
        }
        return tests.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Test? ReadTest(string testId)
    {
        if (!IsSafeId(testId)) return null;
        var path = Path.Combine(Root, testId + ".json");
        if (!File.Exists(path)) return null;
        try { return TestJson.Deserialize(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return null; }
    }

    public bool WriteTest(Test test)
    {
        if (!IsSafeId(test.TestId)) return false;
        return AtomicWriteText(Path.Combine(Root, test.TestId + ".json"), TestJson.Serialize(test));
    }

    public bool DeleteTest(string testId)
    {
        if (!IsSafeId(testId)) return false;
        var path = Path.Combine(Root, testId + ".json");
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public bool IsValid() =>
        Directory.Exists(Root) && Directory.GetFiles(Root, "*.json").Length > 0;

    private static bool AtomicWriteText(string target, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(target);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            return true;
        }
        catch { return false; }
    }
}
