namespace CardioSimulator.Core.Data;

/// <summary>
/// A source that can enumerate its current (merged) content as pack entries, so it can be
/// re-packaged into a distributable encrypted <c>*.pak</c> via <see cref="ContentPackWriter"/>.
/// Implemented by the encrypted read-only sources (verbatim pass-through of the pack) and the
/// writable overlay sources (base overridden by overlay, tombstoned items excluded).
/// </summary>
public interface IContentPackExportable
{
    /// <summary>Entry path → bytes for every entry that should appear in an exported pack.</summary>
    IEnumerable<KeyValuePair<string, byte[]>> ExportEntries();
}
