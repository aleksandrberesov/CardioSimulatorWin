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

    public bool IsValid() =>
        Directory.Exists(Root) && File.Exists(Path.Combine(Root, "manifest.txt"));
}
