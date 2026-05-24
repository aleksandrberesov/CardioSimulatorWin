namespace CardioSimulator.Core.Domain;

/// <summary>
/// 12-lead vocabulary used across the dataset, manifest, and renderer.
/// Declaration order is the canonical lead order
/// (I, II, III, aVR, aVL, aVF, V1..V6).
/// </summary>
public enum Lead
{
    I,
    II,
    III,
    aVR,
    aVL,
    aVF,
    V1,
    V2,
    V3,
    V4,
    V5,
    V6,
}

public static class Leads
{
    /// <summary>All leads in canonical declaration order.</summary>
    public static readonly IReadOnlyList<Lead> All = Enum.GetValues<Lead>();

    /// <summary>
    /// Parses a lead token, trimming whitespace and ignoring case.
    /// Returns null for an unknown token.
    /// </summary>
    public static Lead? FromToken(string raw)
    {
        var trimmed = raw.Trim();
        foreach (var lead in All)
        {
            if (string.Equals(lead.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return lead;
            }
        }
        return null;
    }
}
