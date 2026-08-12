using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// One acronym in the canonical ECG taxonomy — the fixed clinical dictionary the customer supplied
/// (see <c>tools/taxonomy-build/</c>). Each acronym is the join key that ties a rhythm (pathology), a
/// course subsection/lecture, and a test/exam question to the same clinical concept, and the anchor
/// student results roll up through.
/// </summary>
/// <param name="Acronym">Canonical code, upper-case (e.g. <c>2AVB1</c>, <c>MI_ANT</c>). Primary key.</param>
/// <param name="NameRu">Russian display name.</param>
/// <param name="Group">Rhythm-group key, reusing the <c>groups.txt</c> vocabulary
/// (<c>sinus</c>/<c>conduction</c>/<c>infarction</c>/…) so the taxonomy plugs into what the app ships.</param>
/// <param name="Section">Top-level course section number («Раздел N»), derived from
/// <see cref="Subsection"/> so it can never disagree with it.</param>
/// <param name="Subsection">Primary course subsection node, e.g. <c>4.6.2</c>.</param>
/// <param name="SubsectionTitle">Localized (RU) subsection title.</param>
/// <param name="AltSubsections">Extra subsection nodes for acronyms that map to more than one
/// (e.g. <c>WPW</c> → <c>4.11.1</c> primary + <c>8.1</c>). Empty for the common single-node case.</param>
public sealed record TaxonomyEntry(
    string Acronym,
    string NameRu,
    string Group,
    int Section,
    string Subsection,
    string SubsectionTitle,
    IReadOnlyList<string> AltSubsections)
{
    /// <summary>
    /// The two-level key (<c>X.Y</c>) the Learning Scale groups subtopics by — the primary subsection
    /// trimmed to its first two dotted components (<c>4.6.2</c> → <c>4.6</c>; <c>3.2</c> stays
    /// <c>3.2</c>). This is the node student mastery is aggregated into.
    /// </summary>
    public string SubtopicKey => Taxonomy.SubtopicKeyOf(Subsection);
}

/// <summary>
/// The canonical ECG acronym taxonomy: a read-only, case-insensitive dictionary of
/// <see cref="TaxonomyEntry"/> loaded from the embedded <c>Taxonomy.tsv</c> (generated from the
/// customer's source tables). Access the app-wide instance via <see cref="Shared"/>; the pure
/// <see cref="Parse"/> entry point exists for tests. Unknown acronyms simply return null — callers
/// treat an untagged rhythm/question as "not in the taxonomy" rather than an error.
/// </summary>
public sealed class Taxonomy
{
    private readonly IReadOnlyList<TaxonomyEntry> _entries;
    private readonly IReadOnlyDictionary<string, TaxonomyEntry> _byAcronym;

    private Taxonomy(IReadOnlyList<TaxonomyEntry> entries)
    {
        _entries = entries;
        var map = new Dictionary<string, TaxonomyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) map[e.Acronym] = e; // last wins on a (bad) duplicate
        _byAcronym = map;
    }

    /// <summary>Every acronym, in the file's order (section → subsection → acronym).</summary>
    public IReadOnlyList<TaxonomyEntry> Entries => _entries;

    /// <summary>Number of acronyms in the taxonomy.</summary>
    public int Count => _entries.Count;

    /// <summary>Normalizes a raw acronym token to its canonical form (trim + upper-case), or null
    /// when blank. Does <em>not</em> check membership — see <see cref="Find"/>.</summary>
    public static string? Normalize(string? acronym)
    {
        var t = acronym?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.ToUpperInvariant();
    }

    /// <summary>The entry for an acronym (case-insensitive), or null if it is not in the taxonomy.</summary>
    public TaxonomyEntry? Find(string? acronym)
    {
        var key = Normalize(acronym);
        return key is not null && _byAcronym.TryGetValue(key, out var e) ? e : null;
    }

    /// <summary>True when <paramref name="acronym"/> is a known taxonomy code.</summary>
    public bool Contains(string? acronym) => Find(acronym) is not null;

    /// <summary>All acronyms whose primary subsection rolls up into the given subtopic key
    /// (<c>X.Y</c>), e.g. <c>4.6</c> returns 1AVB/PRIE/2AVB/2AVB1/2AVB2/3AVB.</summary>
    public IEnumerable<TaxonomyEntry> ForSubtopic(string subtopicKey) =>
        _entries.Where(e => string.Equals(e.SubtopicKey, subtopicKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>All acronyms in a top-level section («Раздел N»).</summary>
    public IEnumerable<TaxonomyEntry> ForSection(int section) =>
        _entries.Where(e => e.Section == section);

    /// <summary>All acronyms in a rhythm group (groups.txt key).</summary>
    public IEnumerable<TaxonomyEntry> ForGroup(string groupKey) =>
        _entries.Where(e => string.Equals(e.Group, groupKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The two-level subtopic key (<c>X.Y</c>) for a subsection node: the first two dotted
    /// components, e.g. <c>4.6.2</c> → <c>4.6</c>, <c>6.3</c> → <c>6.3</c>, <c>3.2</c> → <c>3.2</c>.
    /// Returns the input trimmed when it has fewer than two components.
    /// </summary>
    public static string SubtopicKeyOf(string subsection)
    {
        var s = subsection?.Trim() ?? string.Empty;
        var parts = s.Split('.');
        return parts.Length >= 2 ? parts[0] + "." + parts[1] : s;
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the tab-separated taxonomy text. Lines starting with <c>#</c> and the header row are
    /// skipped; malformed rows are ignored (tolerant, like the other dataset parsers). Columns:
    /// <c>acronym  name_ru  group  section  subsection  subsection_title  alt_subsections</c>.
    /// </summary>
    public static Taxonomy Parse(string tsv)
    {
        var entries = new List<TaxonomyEntry>();
        foreach (var raw in tsv.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            var c = line.Split('\t');
            if (c.Length < 6) continue;

            var acronym = Normalize(c[0]);
            if (acronym is null || acronym.Equals("ACRONYM", StringComparison.OrdinalIgnoreCase)) continue;

            var subsection = c[4].Trim();
            var section = int.TryParse(c[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : SectionOf(subsection);
            var alt = c.Length > 6
                ? c[6].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();

            entries.Add(new TaxonomyEntry(
                Acronym: acronym,
                NameRu: c[1].Trim(),
                Group: c[2].Trim(),
                Section: section,
                Subsection: subsection,
                SubsectionTitle: c[5].Trim(),
                AltSubsections: alt));
        }
        return new Taxonomy(entries);
    }

    private static int SectionOf(string subsection)
    {
        var head = subsection.Split('.').FirstOrDefault();
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    /// <summary>An empty taxonomy (no acronyms). Used as the safe fallback if the embedded resource
    /// cannot be read, and as an explicit "no taxonomy" argument in tests.</summary>
    public static Taxonomy Empty { get; } = new(Array.Empty<TaxonomyEntry>());

    // ── Shared instance (embedded resource) ─────────────────────────────────

    private static readonly Lazy<Taxonomy> _shared = new(LoadEmbedded);

    /// <summary>The app-wide taxonomy, loaded once from the embedded <c>Taxonomy.tsv</c>. Falls back
    /// to <see cref="Empty"/> if the resource is missing/unreadable (never throws at the call site).</summary>
    public static Taxonomy Shared => _shared.Value;

    private static Taxonomy LoadEmbedded()
    {
        try
        {
            var asm = typeof(Taxonomy).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Taxonomy.tsv", StringComparison.OrdinalIgnoreCase));
            if (name is null) return Empty;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return Empty;
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return Empty;
        }
    }
}
