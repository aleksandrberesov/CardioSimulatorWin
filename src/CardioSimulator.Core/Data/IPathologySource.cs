using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Storage-agnostic source for the flat pathology dataset. Implementations
/// expose a <c>manifest.txt</c> and one <c>.dat</c> file per pathology, both
/// UTF-8.
///
/// This interface is <b>read-only</b> by contract. The editor writes back
/// through <see cref="FilePathologySource.WritePathology"/> on the file-backed
/// implementation — read-only sources cannot be written.
/// </summary>
public interface IPathologySource
{
    PathologyManifest? ReadManifest();
    PathologyFile? ReadPathology(string id);
    IReadOnlyList<string> ListPathologies();
}
