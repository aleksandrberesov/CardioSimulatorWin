using System;
using System.Collections.Generic;
using System.Linq;

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
/// <param name="Number">Optional 1-based clinical-case number, shown as a prefix in the rhythm
/// list and in the clinical dashboard header (<c>Clinical case №N</c>). Null for un-enumerated
/// datasets; assign with <c>tools/pathology-enumerate/enumerate_pathologies.py</c>.</param>
/// <param name="Acronyms">Canonical taxonomy codes (e.g. <c>SB</c>, <c>LVH</c>, <c>MI_ANT</c>) for every
/// finding this rhythm exhibits — see <see cref="Taxonomy"/>. The first is treated as the primary
/// diagnosis (it drives group filing). Null/empty for un-tagged/legacy datasets. Persisted comma-joined
/// in the <c>acronym:</c> manifest field.</param>
public sealed record PathologyEntry(
    string Id,
    string TitleEn,
    string? NameRu,
    int LeadsCount,
    string FileName,
    string? Group = null,
    string? ClinicalCase = null,
    int? Number = null,
    IReadOnlyList<string>? Acronyms = null)
{
    /// <summary>The taxonomy acronyms (never null; empty when untagged). First = primary diagnosis.</summary>
    public IReadOnlyList<string> AcronymList => Acronyms ?? Array.Empty<string>();

    /// <summary>
    /// The Russian display name. Returns authored <see cref="NameRu"/> if set; otherwise falls back to
    /// resolving canonical taxonomy acronyms (<see cref="AcronymList"/>) or translating English title
    /// findings via <see cref="Taxonomy.Shared"/> into a single or composite Russian title.
    /// </summary>
    public string? ResolvedNameRu => PathologyTranslationHelpers.ResolveNameRu(NameRu, AcronymList, TitleEn);
}

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

    /// <summary>Optional group key for the "all rhythms" filter, persisted via the <c>group:</c>
    /// header field and mirrored into the manifest entry on save. Null = ungrouped.</summary>
    public string? Group { get; init; }

    /// <summary>Optional clinical case description containing key-value parameters (e.g. age=45,gender=Male,hr=72,bp=120/80).</summary>
    public string? ClinicalCase { get; init; }

    /// <summary>Optional 1-based clinical-case number, persisted via the <c>number:</c> header
    /// field and mirrored into the manifest entry on save. Null = un-enumerated.</summary>
    public int? Number { get; init; }

    /// <summary>Canonical taxonomy acronyms (e.g. <c>SB</c>, <c>LVH</c>, <c>MI_ANT</c>) for every finding
    /// this rhythm exhibits, persisted comma-joined via the <c>acronym:</c> header field and mirrored into
    /// the manifest entry on save. The first is the primary diagnosis. Null/empty = un-tagged. See
    /// <see cref="Taxonomy"/>.</summary>
    public IReadOnlyList<string>? Acronyms { get; init; }

    /// <summary>The taxonomy acronyms (never null; empty when untagged). First = primary diagnosis.</summary>
    public IReadOnlyList<string> AcronymList => Acronyms ?? Array.Empty<string>();

    /// <summary>
    /// The Russian display name. Returns authored <see cref="NameRu"/> if set; otherwise falls back to
    /// resolving canonical taxonomy acronyms (<see cref="AcronymList"/>) or translating English title
    /// findings via <see cref="Taxonomy.Shared"/> into a single or composite Russian title.
    /// </summary>
    public string? ResolvedNameRu => PathologyTranslationHelpers.ResolveNameRu(NameRu, AcronymList, TitleEn);

    /// <summary>Optional text about pathology, persisted via the <c>description:</c> header field.</summary>
    public string? Description { get; init; }

    /// <summary>Optional authored annotation overlays ("tips"), persisted via the <c>tips:</c> header
    /// field. Defaults to empty. Geometry is in ECG data space (see <see cref="TipOverlay"/>).</summary>
    public IReadOnlyList<TipOverlay> Tips { get; init; } = Array.Empty<TipOverlay>();

    /// <summary>Optional authored text comments/explanations (the "Видим:" list shown on the monitor),
    /// persisted via the <c>tip_notes:</c> header field. Defaults to empty.</summary>
    public IReadOnlyList<string> TipComments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Translation fallback helper for ECG pathologies. Uses canonical taxonomy acronyms and English display
/// names (<see cref="Taxonomy"/>) to construct single or composite Russian titles when explicit
/// <c>NameRu</c> is unauthored.
/// </summary>
public static class PathologyTranslationHelpers
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> TextRuCache = new(StringComparer.Ordinal);

    public static string? ResolveNameRu(
        string? nameRu,
        IReadOnlyList<string>? acronyms,
        string? titleEn = null,
        Taxonomy? taxonomy = null)
    {
        var tax = taxonomy ?? Taxonomy.Shared;

        if (!string.IsNullOrWhiteSpace(nameRu) && !System.Text.RegularExpressions.Regex.IsMatch(nameRu, @"[a-zA-Z]"))
            return nameRu;

        if (!string.IsNullOrWhiteSpace(nameRu))
        {
            var textFromRu = ResolveTextRu(nameRu, tax);
            if (!string.IsNullOrWhiteSpace(textFromRu) && !System.Text.RegularExpressions.Regex.IsMatch(textFromRu, @"[a-zA-Z]"))
            {
                return textFromRu;
            }
        }

        string? textRuFromTitle = null;
        if (!string.IsNullOrWhiteSpace(titleEn))
        {
            textRuFromTitle = ResolveTextRu(titleEn, tax);
            if (!string.IsNullOrWhiteSpace(textRuFromTitle) && !System.Text.RegularExpressions.Regex.IsMatch(textRuFromTitle, @"[a-zA-Z]"))
            {
                var normalizedTitle = System.Text.RegularExpressions.Regex.Replace(titleEn, @"\s+(with|and)\s+", " + ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int titlePartCount = normalizedTitle.Split(new[] { '+', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (acronyms is null || acronyms.Count == 0 || (acronyms.Count == 1 && titlePartCount > 1))
                {
                    return textRuFromTitle;
                }
            }
        }

        if (acronyms is not null && acronyms.Count > 0)
        {
            var parts = new List<string>();
            foreach (var acronym in acronyms)
            {
                var entry = tax.Find(acronym);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.NameRu))
                {
                    if (!parts.Contains(entry.NameRu, StringComparer.OrdinalIgnoreCase))
                        parts.Add(entry.NameRu);
                }
            }
            if (parts.Count > 0) return string.Join(", ", parts);
        }

        if (!string.IsNullOrWhiteSpace(textRuFromTitle))
        {
            return textRuFromTitle;
        }

        if (!string.IsNullOrWhiteSpace(nameRu))
        {
            return ResolveTextRu(nameRu, tax);
        }

        return null;
    }

    /// <summary>
    /// Translates raw English finding strings or compound titles into Russian
    /// using taxonomy acronyms, English finding mappings, and regex phrase rules.
    /// </summary>
    public static string? ResolveTextRu(string? text, Taxonomy? taxonomy = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var input = text.Trim();
        if (taxonomy is null || ReferenceEquals(taxonomy, Taxonomy.Shared))
        {
            return TextRuCache.GetOrAdd(input, key => ResolveTextRuInternal(key, Taxonomy.Shared));
        }
        return ResolveTextRuInternal(input, taxonomy);
    }

    private static string? ResolveTextRuInternal(string input, Taxonomy tax)
    {
        var exact = tax.Find(input) ?? tax.FindByEn(input);
        if (exact is not null && !string.IsNullOrWhiteSpace(exact.NameRu))
            return exact.NameRu;

        var normalizedInput = System.Text.RegularExpressions.Regex.Replace(
            input,
            @"\s+(with|and)\s+",
            " + ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var separators = new[] { '+', ',', ';' };
        var rawParts = normalizedInput.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (rawParts.Length == 0) return null;

        var translatedParts = new List<string>();
        bool anyTranslated = false;

        foreach (var rawPart in rawParts)
        {
            var trimmed = rawPart.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var entry = tax.Find(trimmed) ?? tax.FindByEn(trimmed);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.NameRu))
            {
                if (!translatedParts.Contains(entry.NameRu, StringComparer.OrdinalIgnoreCase))
                    translatedParts.Add(entry.NameRu);
                anyTranslated = true;
                continue;
            }

            var partTranslated = trimmed;
            bool partMatched = false;
            foreach (var rule in Taxonomy.EnglishRules)
            {
                var match = System.Text.RegularExpressions.Regex.Match(partTranslated, rule.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var ruleEntry = tax.Find(rule.Acronym);
                    if (ruleEntry is not null && !string.IsNullOrWhiteSpace(ruleEntry.NameRu))
                    {
                        partTranslated = System.Text.RegularExpressions.Regex.Replace(
                            partTranslated,
                            rule.Pattern,
                            ruleEntry.NameRu,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        partMatched = true;
                    }
                }
            }

            if (partMatched)
            {
                if (!translatedParts.Contains(partTranslated, StringComparer.OrdinalIgnoreCase))
                    translatedParts.Add(partTranslated);
                anyTranslated = true;
            }
            else
            {
                if (!translatedParts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    translatedParts.Add(trimmed);
            }
        }

        if (!anyTranslated) return null;

        var delimiter = normalizedInput.Contains('+') ? " + " : ", ";
        return string.Join(delimiter, translatedParts);
    }
}
