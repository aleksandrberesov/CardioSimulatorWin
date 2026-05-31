using System.Text;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// <see cref="IPathologySource"/> backed by a directory on disk (typically a
/// writable app-data folder, populated by <see cref="ZipExtractor"/>).
///
/// Adds <see cref="WritePathology"/> for the editor save flow — writes are
/// atomic via a <c>.tmp</c> + rename to avoid partial files on interrupt.
/// </summary>
public sealed class FilePathologySource : IPathologySource
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }

    public FilePathologySource(string root)
    {
        Root = root;
    }

    public PathologyManifest? ReadManifest()
    {
        try
        {
            var path = Path.Combine(Root, "manifest.txt");
            if (!File.Exists(path)) return null;
            return PathologyParser.ParseManifest(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public PathologyFile? ReadPathology(string id)
    {
        try
        {
            var path = Path.Combine(Root, $"{id}.dat");
            if (!File.Exists(path)) return null;
            return PathologyParser.ParsePathology(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListPathologies()
    {
        try
        {
            if (!Directory.Exists(Root)) return Array.Empty<string>();
            return Directory.GetFiles(Root, "*.dat")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="file"/> as <c>&lt;id&gt;.dat</c>. Uses the
    /// supplied lead order, else the manifest's, else the canonical order.
    /// </summary>
    public bool WritePathology(PathologyFile file, IReadOnlyList<Lead>? leadOrder = null)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var order = leadOrder ?? ReadManifest()?.LeadOrder ?? Leads.All;
            var text = PathologyParser.SerializePathology(file, order);
            var target = Path.Combine(Root, $"{file.Id}.dat");
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes &lt;id&gt;.dat from disk and updates the manifest. Returns true on success.</summary>
    public bool DeletePathology(string id)
    {
        try
        {
            var datPath = Path.Combine(Root, $"{id}.dat");
            if (File.Exists(datPath)) File.Delete(datPath);
            var manifest = ReadManifest();
            if (manifest is not null)
            {
                var entries = manifest.Entries.Where(e => e.Id != id).ToList();
                WriteManifest(manifest with { Entries = entries });
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Duplicates a pathology under a fresh id (baseId+suffix) and adds a manifest entry.
    /// Returns the new id or null on failure.
    /// </summary>
    public string? DuplicatePathology(string id)
    {
        try
        {
            var file = ReadPathology(id);
            if (file is null) return null;
            var newId = GenerateUniqueId(id + "_copy");
            var newFile = file with { Id = newId };
            if (!WritePathology(newFile)) return null;
            var manifest = ReadManifest();
            if (manifest is not null)
            {
                var origEntry = manifest.Entries.FirstOrDefault(e => e.Id == id);
                if (origEntry is not null)
                {
                    var newEntry = origEntry with { Id = newId, FileName = $"{newId}.dat" };
                    var entries = manifest.Entries.Append(newEntry).ToList();
                    WriteManifest(manifest with { Entries = entries });
                }
            }
            return newId;
        }
        catch
        {
            return null;
        }
    }

    private void WriteManifest(PathologyManifest manifest)
    {
        var text = PathologyParser.SerializeManifest(manifest);
        var target = Path.Combine(Root, "manifest.txt");
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, text, Utf8NoBom);
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
    }

    private string GenerateUniqueId(string baseId)
    {
        var id = baseId;
        var suffix = 1;
        while (File.Exists(Path.Combine(Root, $"{id}.dat")))
        {
            suffix++;
            id = $"{baseId}{suffix}";
        }
        return id;
    }

    public bool IsValid() =>
        Directory.Exists(Root) && File.Exists(Path.Combine(Root, "manifest.txt"));
}
