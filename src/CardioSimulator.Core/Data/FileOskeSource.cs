using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// File-backed OSCE source. Layout under <see cref="Root"/>:
/// <code>
/// forms/&lt;formId&gt;.json            – form templates (therapy.json, cardiology.json)
/// answers/&lt;ecgId&gt;/&lt;formId&gt;.json  – per-ECG answer keys
/// </code>
/// All writes are atomic (temp file + move), mirroring <see cref="FileCourseSource"/>. Saved results
/// live separately in <see cref="OskeResultStore"/>.
/// </summary>
public class FileOskeSource : IOskeSource
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public FileOskeSource(string root)
    {
        Root = root;
    }

    private string FormsDir => Path.Combine(Root, "forms");
    private string AnswersDir => Path.Combine(Root, "answers");

    /// <summary>Rejects ids that could escape their folder (path traversal / separators).</summary>
    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        id is not "." and not "..";

    public IReadOnlyList<OskeForm> ReadForms()
    {
        if (!Directory.Exists(FormsDir)) return Array.Empty<OskeForm>();
        var forms = new List<OskeForm>();
        foreach (var path in Directory.GetFiles(FormsDir, "*.json"))
        {
            try
            {
                if (OskeJson.DeserializeForm(File.ReadAllText(path, Encoding.UTF8)) is { } form)
                    forms.Add(form);
            }
            catch
            {
                // skip an unreadable form file
            }
        }
        return forms;
    }

    public OskeForm? ReadForm(string formId)
    {
        if (!IsSafeId(formId)) return null;
        var path = Path.Combine(FormsDir, formId + ".json");
        if (!File.Exists(path)) return null;
        try { return OskeJson.DeserializeForm(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return null; }
    }

    public OskeAnswerKey? ReadAnswerKey(string ecgId, string formId)
    {
        if (!IsSafeId(ecgId) || !IsSafeId(formId)) return null;
        var path = Path.Combine(AnswersDir, ecgId, formId + ".json");
        if (!File.Exists(path)) return null;
        try { return OskeJson.DeserializeAnswerKey(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return null; }
    }

    public IReadOnlyList<string> ListAnswerKeyEcgIds(string formId)
    {
        if (!IsSafeId(formId) || !Directory.Exists(AnswersDir)) return Array.Empty<string>();
        return Directory.GetDirectories(AnswersDir)
            .Where(dir => File.Exists(Path.Combine(dir, formId + ".json")))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool WriteForm(OskeForm form)
    {
        if (!IsSafeId(form.FormId)) return false;
        return AtomicWriteText(Path.Combine(FormsDir, form.FormId + ".json"), OskeJson.SerializeForm(form));
    }

    public bool WriteAnswerKey(OskeAnswerKey key)
    {
        if (!IsSafeId(key.EcgId) || !IsSafeId(key.FormId)) return false;
        return AtomicWriteText(Path.Combine(AnswersDir, key.EcgId, key.FormId + ".json"),
            OskeJson.SerializeAnswerKey(key));
    }

    public bool DeleteAnswerKey(string ecgId, string formId)
    {
        if (!IsSafeId(ecgId) || !IsSafeId(formId)) return false;
        var path = Path.Combine(AnswersDir, ecgId, formId + ".json");
        try
        {
            if (File.Exists(path)) File.Delete(path);
            // Tidy up an emptied ecg folder.
            var dir = Path.Combine(AnswersDir, ecgId);
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
            return true;
        }
        catch { return false; }
    }

    public bool IsValid() =>
        Directory.Exists(FormsDir) && Directory.GetFiles(FormsDir, "*.json").Length > 0;

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
