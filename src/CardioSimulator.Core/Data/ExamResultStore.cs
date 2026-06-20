using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Persists graded examination attempts as one JSON file per attempt under <see cref="Root"/>. File
/// name: <c>yyyy-MM-ddTHH-mm-ss_&lt;ФИО&gt;.json</c>. The results sub-section reads these back. A direct
/// port of <see cref="OskeResultStore"/> for the examination pipeline.
/// </summary>
public class ExamResultStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public ExamResultStore(string root)
    {
        Root = root;
    }

    public bool Save(ExamResult result)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var stamp = result.Timestamp.ToString("yyyy-MM-ddTHH-mm-ss");
            var name = $"{stamp}_{Sanitize(result.Student.FullName)}.json";
            var target = Path.Combine(Root, name);
            var n = 1;
            while (File.Exists(target))
                target = Path.Combine(Root, $"{stamp}_{Sanitize(result.Student.FullName)}_{n++}.json");

            var tmp = target + ".tmp";
            File.WriteAllText(tmp, TestJson.SerializeExamResult(result), Utf8NoBom);
            File.Move(tmp, target);
            return true;
        }
        catch { return false; }
    }

    /// <summary>All saved results, newest first. Unreadable files are skipped.</summary>
    public IReadOnlyList<ExamResult> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<ExamResult>();
        var results = new List<ExamResult>();
        foreach (var path in Directory.GetFiles(Root, "*.json"))
        {
            try
            {
                if (TestJson.DeserializeExamResult(File.ReadAllText(path, Encoding.UTF8)) is { } r)
                    results.Add(r);
            }
            catch
            {
                // skip an unreadable result file
            }
        }
        return results.OrderByDescending(r => r.Timestamp).ToList();
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Select(ch => Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch)
            .ToArray())
            .Replace(' ', '_')
            .Trim('_');
        if (cleaned.Length == 0) cleaned = "anon";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
