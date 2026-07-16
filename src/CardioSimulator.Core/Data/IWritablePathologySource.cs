using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// A pathology source that also supports the constructor's write operations. Implemented by
/// <see cref="FilePathologySource"/> (loose author files) and <see cref="OverlayPathologySource"/>
/// (an encrypted writable overlay layered over a read-only content pack).
///
/// <para><see cref="PathologyRepository"/> routes every write through this interface, so the
/// constructor works against whichever storage is active without knowing whether it is editing
/// loose files or an encrypted overlay.</para>
/// </summary>
public interface IWritablePathologySource : IPathologySource
{
    bool WritePathology(PathologyFile file, IReadOnlyList<Lead>? leadOrder = null);
    bool DeletePathology(string id);
    string? ImportPathology(PathologyFile file);
    string? DuplicatePathology(string id);
    string? CreatePathology(string titleEn, string? nameRu, int sampleCount, int baseline);
}
