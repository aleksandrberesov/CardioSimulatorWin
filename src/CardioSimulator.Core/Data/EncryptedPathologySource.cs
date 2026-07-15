using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read-only <see cref="IPathologySource"/> backed by an encrypted content pack
/// (<see cref="EncryptedArchive"/>) held entirely in memory. This is the distribution read path:
/// the bundled dataset ships as <c>Pathologies.pak</c> and is never extracted to disk, so no
/// plaintext <c>.dat</c> ever lands in <c>%LOCALAPPDATA%</c>.
///
/// <para>Being read-only, it does not implement the editor write/create/delete methods that
/// <see cref="FilePathologySource"/> adds. Authoring runs against a file-backed source instead
/// (the vendor edits loose files, then re-packs).</para>
/// </summary>
public sealed class EncryptedPathologySource : IPathologySource, IDisposable
{
    private readonly EncryptedArchive _archive;

    public EncryptedPathologySource(EncryptedArchive archive)
    {
        _archive = archive;
    }

    /// <summary>Opens the pack at <paramref name="packPath"/> and wraps it. Throws on a bad pack.</summary>
    public static EncryptedPathologySource Open(string packPath) =>
        new(EncryptedArchive.Open(packPath));

    public PathologyManifest? ReadManifest()
    {
        try
        {
            var text = _archive.ReadByNameText("manifest.txt");
            return text is null ? null : PathologyParser.ParseManifest(text);
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
            var text = _archive.ReadByNameText($"{id}.dat");
            return text is null ? null : PathologyParser.ParsePathology(text);
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
            return _archive.FileNamesWithExtension(".dat")
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

    /// <summary>The pack's <c>groups.txt</c> text (rhythm-group catalog), or null if absent. Fed to
    /// the UI's group catalog so grouping survives without the file being extracted to disk.</summary>
    public string? ReadGroupsText() => _archive.ReadByNameText("groups.txt");

    /// <summary>True once the pack yields a manifest — mirrors <see cref="FilePathologySource.IsValid"/>.</summary>
    public bool IsValid() => ReadManifest() is not null;

    public void Dispose() => _archive.Dispose();
}
