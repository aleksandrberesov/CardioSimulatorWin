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
/// <param name="Group">Optional grouping key for the "all rhythms" group filter (e.g.
/// <c>conduction</c>, <c>infarction</c>). Null for ungrouped/legacy datasets.</param>
public sealed record PathologyEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    int LeadsCount,
    string FileName,
    string? Group = null);

/// <summary>
/// A placed ECG element recorded as a re-editable annotation over a lead's samples. The samples
/// remain the render source of truth; this records what was generated and where (start/length in
/// sample indices, height in mV) so width/height can be re-applied later. Persisted via the lead
/// block's <c>elements:</c> field, mirroring how <see cref="SignificantPoint"/> uses <c>markers:</c>.
/// </summary>
public sealed record EcgElementInstance(EcgElement Type, int StartIndex, int Length, float AmplitudeMv);

/// <summary>One lead block inside a <c>&lt;pathology&gt;.dat</c> file.</summary>
public sealed class LeadStream : IEquatable<LeadStream>
{
    public Lead Lead { get; }

    /// <summary>Raw ADC samples, baseline-centered on 1024.</summary>
    public int[] Samples { get; }

    /// <summary>Placed ECG elements annotating this lead (optional; empty by default).</summary>
    public IReadOnlyList<EcgElementInstance> Elements { get; }

    public LeadStream(Lead lead, int[] samples, IReadOnlyList<EcgElementInstance>? elements = null)
    {
        Lead = lead;
        Samples = samples;
        Elements = elements ?? Array.Empty<EcgElementInstance>();
    }

    /// <summary>Returns a copy of this stream with a new sample buffer (elements preserved).</summary>
    public LeadStream WithSamples(int[] samples) => new(Lead, samples, Elements);

    /// <summary>Returns a copy of this stream with a new element annotation list (samples preserved).</summary>
    public LeadStream WithElements(IReadOnlyList<EcgElementInstance> elements) => new(Lead, Samples, elements);

    public bool Equals(LeadStream? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Lead == other.Lead
            && Samples.AsSpan().SequenceEqual(other.Samples)
            && Elements.SequenceEqual(other.Elements);
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
    IReadOnlyDictionary<Lead, LeadStream> Leads)
{
    /// <summary>
    /// Optional ECG annotation markers (peaks + boundaries), persisted via the <c>markers:</c>
    /// header field. Defaults to empty. Mirrors the Android <c>significantPoints</c> field.
    /// </summary>
    public IReadOnlyList<SignificantPoint> SignificantPoints { get; init; } = Array.Empty<SignificantPoint>();
}
