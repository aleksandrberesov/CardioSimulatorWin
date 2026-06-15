using System.Collections.Generic;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read access to OSCE content (form templates + per-ECG answer keys). Mirrors
/// <see cref="ICourseSource"/>; writes live on the concrete <see cref="FileOskeSource"/>.
/// </summary>
public interface IOskeSource
{
    IReadOnlyList<OskeForm> ReadForms();
    OskeForm? ReadForm(string formId);
    OskeAnswerKey? ReadAnswerKey(string ecgId, string formId);

    /// <summary>Ecg ids that have an authored answer key for <paramref name="formId"/>.</summary>
    IReadOnlyList<string> ListAnswerKeyEcgIds(string formId);

    bool IsValid();
}
