using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Persists graded OSCE attempts as one JSON file per attempt under <see cref="Root"/>
/// (Николай's «сохраняются в файле»). File name: <c>yyyy-MM-ddTHH-mm-ss_&lt;ФИО&gt;.json</c>. The
/// results sub-section reads these back. A later DB-backed admin can import the same files.
/// </summary>
public class OskeResultStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public OskeResultStore(string root)
    {
        Root = root;
    }

    /// <summary>A saved result paired with the file that backs it, so callers can delete or rewrite it.</summary>
    public sealed record Entry(string Path, OskeResult Result);

    public bool Save(OskeResult result)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var stamp = result.Timestamp.ToString("yyyy-MM-ddTHH-mm-ss");
            var name = $"{stamp}_{Sanitize(result.Student.FullName)}.json";
            var target = Path.Combine(Root, name);
            // Disambiguate if two attempts collide within the same second.
            var n = 1;
            while (File.Exists(target))
                target = Path.Combine(Root, $"{stamp}_{Sanitize(result.Student.FullName)}_{n++}.json");

            var tmp = target + ".tmp";
            File.WriteAllText(tmp, OskeJson.SerializeResult(result), Utf8NoBom);
            File.Move(tmp, target);
            return true;
        }
        catch { return false; }
    }

    /// <summary>All saved results, newest first. Unreadable files are skipped.</summary>
    public IReadOnlyList<OskeResult> List() => ListEntries().Select(e => e.Result).ToList();

    /// <summary>All saved results with their backing file paths, newest first. Unreadable files are
    /// skipped. Callers use the paths with <see cref="Delete"/> / <see cref="Overwrite"/>.</summary>
    public IReadOnlyList<Entry> ListEntries()
    {
        if (!Directory.Exists(Root)) return Array.Empty<Entry>();
        var entries = new List<Entry>();
        foreach (var path in Directory.GetFiles(Root, "*.json"))
        {
            try
            {
                if (OskeJson.DeserializeResult(File.ReadAllText(path, Encoding.UTF8)) is { } r)
                    entries.Add(new Entry(path, r));
            }
            catch
            {
                // skip an unreadable result file
            }
        }
        return entries.OrderByDescending(e => e.Result.Timestamp).ToList();
    }

    /// <summary>Deletes a single saved result file. Returns true if it is gone afterwards
    /// (already-missing counts as success); false only on an IO/locking failure.</summary>
    public bool Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Rewrites a saved result in place — used to edit an attempt's student info or grade
    /// without spawning a second file. The file name (timestamp + original ФИО) is left as-is; the
    /// results viewer reads identity from the JSON body. Returns false on an IO error.</summary>
    public bool Overwrite(string path, OskeResult result)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, OskeJson.SerializeResult(result), Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Deletes every saved result file (locked files are skipped). Returns the number removed.</summary>
    public int Clear()
    {
        if (!Directory.Exists(Root)) return 0;
        var removed = 0;
        foreach (var path in Directory.GetFiles(Root, "*.json"))
        {
            try { File.Delete(path); removed++; }
            catch { /* skip a locked file */ }
        }
        return removed;
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
