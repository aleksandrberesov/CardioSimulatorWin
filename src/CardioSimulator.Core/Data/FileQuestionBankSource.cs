using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// File-backed question bank. Layout under <see cref="Root"/> (<c>tests/bank</c>):
/// <code>
/// &lt;questionId&gt;.json   – one bank question per file
/// themes.json           – the editable theme catalog (NOT a question; skipped here)
/// </code>
/// All writes are atomic (temp file + move), mirroring <see cref="FileTestSource"/>. The bank lives in
/// a subfolder of <c>tests/</c> so the non-recursive <c>*.json</c> scan in
/// <see cref="FileTestSource.ReadTests"/> never picks bank questions up as tests.
/// </summary>
public class FileQuestionBankSource : IQuestionBankSource
{
    /// <summary>The theme-catalog file lives alongside the questions but is not a question.</summary>
    public const string ThemesFileName = "themes.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public FileQuestionBankSource(string root)
    {
        Root = root;
    }

    /// <summary>Rejects ids that could escape the folder (path traversal / separators).</summary>
    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        id is not "." and not "..";

    public IReadOnlyList<TestQuestion> ReadQuestions()
    {
        if (!Directory.Exists(Root)) return Array.Empty<TestQuestion>();
        var questions = new List<TestQuestion>();
        foreach (var path in Directory.GetFiles(Root, "*.json"))
        {
            if (string.Equals(Path.GetFileName(path), ThemesFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (TestJson.DeserializeQuestion(File.ReadAllText(path, Encoding.UTF8)) is { } q)
                    questions.Add(q);
            }
            catch
            {
                // skip an unreadable question file
            }
        }
        return questions
            .OrderBy(q => q.Theme ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(q => q.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public TestQuestion? ReadQuestion(string id)
    {
        if (!IsSafeId(id)) return null;
        var path = Path.Combine(Root, id + ".json");
        if (!File.Exists(path)) return null;
        try { return TestJson.DeserializeQuestion(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return null; }
    }

    public bool WriteQuestion(TestQuestion question)
    {
        if (!IsSafeId(question.Id)) return false;
        return AtomicWriteText(Path.Combine(Root, question.Id + ".json"), TestJson.SerializeQuestion(question));
    }

    public bool DeleteQuestion(string id)
    {
        if (!IsSafeId(id)) return false;
        var path = Path.Combine(Root, id + ".json");
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public bool IsValid() => Directory.Exists(Root);

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
