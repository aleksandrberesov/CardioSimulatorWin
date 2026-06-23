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
    /// supplied lead order, else the manifest's, else the canonical order. Also keeps
    /// <c>manifest.txt</c> in sync (title/name changed, or a new entry) so renamed or newly
    /// created pathologies surface in the lists, which render from manifest entries rather than
    /// the <c>.dat</c> header. Port of Android <c>FilePathologySource.writePathology</c>.
    /// </summary>
    public bool WritePathology(PathologyFile file, IReadOnlyList<Lead>? leadOrder = null)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var manifest = ReadManifest();
            var order = leadOrder ?? manifest?.LeadOrder ?? Leads.All;
            var text = PathologyParser.SerializePathology(file, order);
            var target = Path.Combine(Root, $"{file.Id}.dat");
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);

            if (manifest is not null)
            {
                var existing = manifest.Entries.FirstOrDefault(e => e.Id == file.Id);
                IReadOnlyList<PathologyEntry>? updatedEntries = null;
                if (existing is not null)
                {
                    if (existing.TitleEn != file.TitleEn || existing.NameRu != file.NameRu || existing.Group != file.Group)
                    {
                        updatedEntries = manifest.Entries
                            .Select(e => e.Id == file.Id
                                ? e with { TitleEn = file.TitleEn, NameRu = file.NameRu, Group = file.Group }
                                : e)
                            .ToList();
                    }
                }
                else
                {
                    updatedEntries = manifest.Entries
                        .Append(new PathologyEntry(file.Id, file.TitleEn, file.NameRu, file.Leads.Count, $"{file.Id}.dat", file.Group))
                        .ToList();
                }
                if (updatedEntries is not null)
                {
                    WriteManifest(manifest with { Entries = updatedEntries });
                }
            }
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
    /// Imports an externally produced pathology (e.g. converted from a WFDB record) under a fresh,
    /// unique id derived from its existing id/title. Writes the <c>.dat</c> and a manifest entry via
    /// <see cref="WritePathology"/>. Returns the new id or null on failure.
    /// </summary>
    public string? ImportPathology(PathologyFile file)
    {
        try
        {
            var seed = string.IsNullOrWhiteSpace(file.Id) ? file.TitleEn : file.Id;
            var newId = GenerateUniqueId(SanitizeId(seed));
            return WritePathology(file with { Id = newId }) ? newId : null;
        }
        catch
        {
            return null;
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

    /// <summary>
    /// Creates a new blank pathology with all leads at baseline, writes the file, and adds a manifest entry.
    /// Returns the new id or null on failure.
    /// </summary>
    public string? CreatePathology(string titleEn, string? nameRu, int sampleCount, int baseline)
    {
        try
        {
            var newId = GenerateUniqueId(SanitizeId(titleEn));
            var flat = new int[sampleCount];
            Array.Fill(flat, baseline);
            var manifest = ReadManifest();
            var order = manifest?.LeadOrder ?? Leads.All;
            var leads = order.ToDictionary(l => l, l => new LeadStream(l, (int[])flat.Clone()));
            var file = new PathologyFile(newId, titleEn, nameRu, leads);
            if (!WritePathology(file)) return null;
            if (manifest is not null)
            {
                var entry = new PathologyEntry(newId, titleEn, nameRu, leads.Count, $"{newId}.dat");
                var entries = manifest.Entries.Append(entry).ToList();
                WriteManifest(manifest with { Entries = entries });
            }
            return newId;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeId(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var id = new string(chars).Trim('_');
        return string.IsNullOrEmpty(id) ? "new_pathology" : id;
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
