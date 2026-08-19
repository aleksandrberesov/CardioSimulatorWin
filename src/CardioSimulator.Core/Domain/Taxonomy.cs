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
/// <param name="NameEn">English display name, from the customer's xlsx source of truth. The base
/// other (non-RU) languages localize from; may be empty if the source does not name the acronym.</param>
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
    string NameEn,
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
    /// All acronym entries matching a course subsection node (e.g. <c>4.6.2</c>), its 2-level subtopic key
    /// (<c>4.6</c>), its section number (<c>4</c>), or listed in its <see cref="TaxonomyEntry.AltSubsections"/>.
    /// </summary>
    public IEnumerable<TaxonomyEntry> ForSubsectionOrTopic(string? subsectionOrKey)
    {
        if (string.IsNullOrWhiteSpace(subsectionOrKey)) return Enumerable.Empty<TaxonomyEntry>();
        var key = subsectionOrKey.Trim();

        // 1. Direct match on subsection, SubtopicKey, or AltSubsections
        var direct = _entries.Where(e =>
            string.Equals(e.Subsection, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.SubtopicKey, key, StringComparison.OrdinalIgnoreCase) ||
            e.AltSubsections.Any(alt => string.Equals(alt, key, StringComparison.OrdinalIgnoreCase))).ToList();

        if (direct.Count > 0) return direct;

        // 2. Subtopic key match (e.g. "4.6.2" -> subtopic "4.6")
        var subKey = SubtopicKeyOf(key);
        var subMatches = _entries.Where(e =>
            string.Equals(e.SubtopicKey, subKey, StringComparison.OrdinalIgnoreCase) ||
            e.AltSubsections.Any(alt => string.Equals(SubtopicKeyOf(alt), subKey, StringComparison.OrdinalIgnoreCase))).ToList();

        if (subMatches.Count > 0) return subMatches;

        // 3. Section number match (e.g. "4" or section extracted from "4.6")
        if (int.TryParse(key.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
        {
            return ForSection(sec);
        }

        return Enumerable.Empty<TaxonomyEntry>();
    }

    /// <summary>
    /// Finds all pathology IDs from <paramref name="pathologies"/> whose <see cref="PathologyEntry.AcronymList"/>
    /// contains at least one code matching any of the <paramref name="acronyms"/>.
    /// </summary>
    public static IReadOnlyList<string> ResolvePathologyIdsForAcronyms(
        IEnumerable<string> acronyms,
        IEnumerable<PathologyEntry> pathologies)
    {
        var acronymSet = new HashSet<string>(
            acronyms.Select(a => Normalize(a)).Where(a => a is not null)!,
            StringComparer.OrdinalIgnoreCase);
        if (acronymSet.Count == 0) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var p in pathologies)
        {
            if (p.AcronymList.Any(a => acronymSet.Contains(a)))
            {
                result.Add(p.Id);
            }
        }
        return result;
    }

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
    /// <c>acronym  name_ru  group  section  subsection  subsection_title  alt_subsections  name_en</c>.
    /// <c>name_en</c> is a trailing column (added later); rows without it parse with an empty English name.
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
            var nameEn = c.Length > 7 ? c[7].Trim() : string.Empty;

            entries.Add(new TaxonomyEntry(
                Acronym: acronym,
                NameRu: c[1].Trim(),
                NameEn: nameEn,
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
