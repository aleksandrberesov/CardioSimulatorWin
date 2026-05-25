namespace CardioSimulator.Core.Domain;

/// <summary>
/// Standard significant points and boundaries of an ECG complex. Member names are the on-disk
/// tokens used by the <c>markers:</c> field in <c>.dat</c> files, so they must match the Android
/// <c>EcgPointType</c> names exactly for cross-platform round-trip. Mirrors the Android enum
/// in <c>domain/SignificantPoint.kt</c>.
/// </summary>
public enum EcgPointType
{
    P_START,
    P_PEAK,
    P_END,
    QRS_START,
    Q_PEAK,
    R_PEAK,
    S_PEAK,
    QRS_END,
    T_START,
    T_PEAK,
    T_END,
}

/// <summary>Helpers for <see cref="EcgPointType"/> (enum-with-fields ported as enum + extensions).</summary>
public static class EcgPointTypes
{
    /// <summary>
    /// Short display label, identical to the Android source (may contain <c>&lt;sub&gt;</c> tags,
    /// which callers strip for plain-text rendering).
    /// </summary>
    public static string Label(this EcgPointType type) => type switch
    {
        EcgPointType.P_START => "P<sub>s</sub>",
        EcgPointType.P_PEAK => "P",
        EcgPointType.P_END => "P<sub>e</sub>",
        EcgPointType.QRS_START => "QRS<sub>s</sub>",
        EcgPointType.Q_PEAK => "Q",
        EcgPointType.R_PEAK => "R",
        EcgPointType.S_PEAK => "S",
        EcgPointType.QRS_END => "QRS<sub>e</sub>",
        EcgPointType.T_START => "T<sub>s</sub>",
        EcgPointType.T_PEAK => "T",
        EcgPointType.T_END => "T<sub>e</sub>",
        _ => type.ToString(),
    };

    /// <summary>Parses an on-disk marker token (an exact enum name) or returns null if unknown.</summary>
    public static EcgPointType? FromToken(string raw)
    {
        var token = raw.Trim();
        foreach (var name in Enum.GetNames<EcgPointType>())
        {
            if (name == token) return Enum.Parse<EcgPointType>(name);
        }
        return null;
    }
}

/// <summary>A marker for a specific sample index in a waveform.</summary>
public sealed record SignificantPoint(int Index, EcgPointType Type);
