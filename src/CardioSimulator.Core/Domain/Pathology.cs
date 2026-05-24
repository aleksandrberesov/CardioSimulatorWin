namespace CardioSimulator.Core.Domain;

/// <summary>
/// Pathology dataset domain types. Mirrors the flat <c>.dat</c> format
/// documented in <c>docs/data-structure.md</c>:
/// <list type="bullet">
/// <item>One <c>.dat</c> file per pathology.</item>
/// <item>All 12 standard leads inside (one exception: <c>emd</c> ships only 6 limb leads).</item>
/// <item>Raw ADC samples, baseline-centered on 1024.</item>
/// <item>No anchors, no part/series indirection, no per-record calibration.</item>
/// </list>
/// </summary>
public sealed record PathologyManifest(
    string Version,
    int Baseline,
    IReadOnlyList<Lead> LeadOrder,
    IReadOnlyList<PathologyEntry> Entries)
{
    /// <summary>Manifest version this build understands; validated on parse.</summary>
    public const string SupportedVersion = "1.0";
}

/// <summary>One row of <see cref="PathologyManifest.Entries"/>.</summary>
public sealed record PathologyEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    int LeadsCount,
    string FileName);

/// <summary>One lead block inside a <c>&lt;pathology&gt;.dat</c> file.</summary>
public sealed class LeadStream : IEquatable<LeadStream>
{
    public Lead Lead { get; }

    /// <summary>Raw ADC samples, baseline-centered on 1024.</summary>
    public int[] Samples { get; }

    public LeadStream(Lead lead, int[] samples)
    {
        Lead = lead;
        Samples = samples;
    }

    /// <summary>Returns a copy of this stream with a new sample buffer.</summary>
    public LeadStream WithSamples(int[] samples) => new(Lead, samples);

    public bool Equals(LeadStream? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Lead == other.Lead && Samples.AsSpan().SequenceEqual(other.Samples);
    }

    public override bool Equals(object? obj) => Equals(obj as LeadStream);

    public override int GetHashCode()
    {
        var hash = (int)Lead;
        foreach (var sample in Samples)
        {
            hash = 31 * hash + sample;
        }
        return hash;
    }
}

/// <summary>Parsed <c>&lt;pathology&gt;.dat</c>.</summary>
public sealed record PathologyFile(
    string Id,
    string TitleEn,
    string? NameRu,
    IReadOnlyDictionary<Lead, LeadStream> Leads);
