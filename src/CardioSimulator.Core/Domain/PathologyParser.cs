using System.Globalization;
using System.Text;

namespace CardioSimulator.Core.Domain;

/// <summary>Thrown when manifest or <c>.dat</c> text violates the expected grammar.</summary>
public sealed class PathologyFormatException : Exception
{
    public PathologyFormatException(string message) : base(message) { }
}

/// <summary>
/// Pure parser + serializer for the flat pathology dataset format.
/// Grammar is documented in <c>docs/data-structure.md</c> §2 (.dat files) and
/// §3 (manifest.txt). Both files are UTF-8, LF, <c>key:value</c> per line, with
/// blank lines separating blocks.
/// </summary>
public static class PathologyParser
{
    // ─── manifest.txt ───────────────────────────────────────────────────

    public static PathologyManifest ParseManifest(string text)
    {
        var (header, body) = SplitHeader(text);

        var version = Get(header, "version")
            ?? throw new PathologyFormatException("manifest: missing 'version'");
        if (version != PathologyManifest.SupportedVersion)
        {
            throw new PathologyFormatException(
                $"manifest: unsupported version '{version}' (this build needs " +
                $"'{PathologyManifest.SupportedVersion}')");
        }

        var baseline = ToIntOrNull(Get(header, "baseline")?.Trim())
            ?? throw new PathologyFormatException("manifest: missing or non-integer 'baseline'");

        var leadOrderRaw = Get(header, "lead_order")
            ?? throw new PathologyFormatException("manifest: missing 'lead_order'");
        var leadOrder = leadOrderRaw
            .Split(',')
            .Select(Leads.FromToken)
            .Where(l => l is not null)
            .Select(l => l!.Value)
            .ToList();

        var entries = new List<PathologyEntry>();
        foreach (var line in body)
        {
            var fields = ParseSemicolonFields(line);
            var id = Get(fields, "pathology");
            if (id is null) continue;
            entries.Add(new PathologyEntry(
                Id: id,
                TitleEn: Get(fields, "title") ?? string.Empty,
                NameRu: Get(fields, "name"),
                LeadsCount: ToIntOrNull(Get(fields, "leads")) ?? 0,
                FileName: $"{id}.dat"));
        }

        return new PathologyManifest(version, baseline, leadOrder, entries);
    }

    public static string SerializeManifest(PathologyManifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("version:").Append(manifest.Version).Append('\n');
        sb.Append("baseline:").Append(manifest.Baseline.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("lead_order:")
            .Append(string.Join(",", manifest.LeadOrder.Select(l => l.ToString())))
            .Append('\n');
        sb.Append("pathologies:").Append(manifest.Entries.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n');
        foreach (var e in manifest.Entries)
        {
            sb.Append("pathology:").Append(e.Id)
                .Append(";leads:").Append(e.LeadsCount.ToString(CultureInfo.InvariantCulture))
                .Append(";title:").Append(e.TitleEn);
            if (!string.IsNullOrWhiteSpace(e.NameRu))
            {
                sb.Append(";name:").Append(e.NameRu);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── <pathology>.dat ────────────────────────────────────────────────

    public static PathologyFile ParsePathology(string text)
    {
        var blocks = SplitBlocks(text);
        if (blocks.Count == 0) throw new PathologyFormatException("pathology: empty file");

        var header = blocks[0];
        var id = Get(header, "pathology")
            ?? throw new PathologyFormatException("pathology: missing 'pathology'");
        var title = Get(header, "title") ?? string.Empty;
        var name = Get(header, "name");
        var markers = ParseMarkers(Get(header, "markers"));

        var leads = new Dictionary<Lead, LeadStream>();
        for (var b = 1; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var leadToken = Get(block, "lead");
            if (leadToken is null) continue;
            var lead = Leads.FromToken(leadToken)
                ?? throw new PathologyFormatException($"pathology[{id}]: unknown lead '{leadToken}'");
            var count = ToIntOrNull(Get(block, "count")?.Trim())
                ?? throw new PathologyFormatException($"pathology[{id}]: lead {lead} missing 'count'");
            var pointsField = Get(block, "points")
                ?? throw new PathologyFormatException($"pathology[{id}]: lead {lead} missing 'points'");
            var samples = ParseIntCsv(pointsField);
            if (samples.Length != count)
            {
                throw new PathologyFormatException(
                    $"pathology[{id}]: lead {lead} 'count' says {count} but parsed {samples.Length} samples");
            }
            leads[lead] = new LeadStream(lead, samples);
        }
        return new PathologyFile(id, title, name, leads) { SignificantPoints = markers };
    }

    public static string SerializePathology(PathologyFile file, IReadOnlyList<Lead> leadOrder)
    {
        var sb = new StringBuilder();
        sb.Append("pathology:").Append(file.Id).Append('\n');
        sb.Append("title:").Append(file.TitleEn).Append('\n');
        sb.Append("name:").Append(file.NameRu ?? string.Empty).Append('\n');
        sb.Append("leads:").Append(file.Leads.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (file.SignificantPoints.Count > 0)
        {
            sb.Append("markers:");
            for (var i = 0; i < file.SignificantPoints.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var pt = file.SignificantPoints[i];
                sb.Append(pt.Index.ToString(CultureInfo.InvariantCulture)).Append(':').Append(pt.Type.ToString());
            }
            sb.Append('\n');
        }
        foreach (var lead in leadOrder)
        {
            if (!file.Leads.TryGetValue(lead, out var stream)) continue;
            sb.Append('\n');
            sb.Append("lead:").Append(lead.ToString()).Append('\n');
            sb.Append("count:").Append(stream.Samples.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("points:");
            for (var i = 0; i < stream.Samples.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(stream.Samples[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the header <c>key:value</c> map and the list of remaining
    /// non-empty lines (the per-pathology index section).
    /// </summary>
    private static (Dictionary<string, string> Header, List<string> Body) SplitHeader(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var header = new Dictionary<string, string>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; break; }
            var kv = SplitKeyValue(line);
            if (kv is null) { i++; continue; }
            header[kv.Value.Key] = kv.Value.Value;
            i++;
        }
        var body = lines.Skip(i).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return (header, body);
    }

    /// <summary>
    /// Splits a <c>.dat</c> text into its header block + per-lead blocks. Each
    /// block becomes a <c>key→value</c> map. Blank lines separate blocks.
    /// </summary>
    private static List<Dictionary<string, string>> SplitBlocks(string text)
    {
        var outBlocks = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    outBlocks.Add(current);
                    current = new Dictionary<string, string>();
                }
                continue;
            }
            var kv = SplitKeyValue(line);
            if (kv is null) continue;
            current[kv.Value.Key] = kv.Value.Value;
        }
        if (current.Count > 0) outBlocks.Add(current);
        return outBlocks;
    }

    private static (string Key, string Value)? SplitKeyValue(string line)
    {
        var i = line.IndexOf(':');
        if (i <= 0) return null;
        return (line.Substring(0, i).Trim(), line.Substring(i + 1));
    }

    private static Dictionary<string, string> ParseSemicolonFields(string line)
    {
        var map = new Dictionary<string, string>();
        foreach (var field in line.Split(';'))
        {
            var kv = SplitKeyValue(field);
            if (kv is null) continue;
            map[kv.Value.Key.Trim()] = kv.Value.Value.Trim();
        }
        return map;
    }

    private static int[] ParseIntCsv(string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<int>();
        var tokens = field.Split(',');
        var outArr = new int[tokens.Length];
        var n = 0;
        foreach (var t in tokens)
        {
            if (int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                outArr[n++] = parsed;
            }
        }
        return n == outArr.Length ? outArr : outArr[..n];
    }

    /// <summary>
    /// Parses the <c>markers:</c> field (<c>index:TYPE,index:TYPE,…</c>). Skips tokens with a
    /// bad shape, a non-integer index, or an unknown type — mirroring the Android parser.
    /// </summary>
    private static IReadOnlyList<SignificantPoint> ParseMarkers(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<SignificantPoint>();
        var outList = new List<SignificantPoint>();
        foreach (var token in field.Split(','))
        {
            var parts = token.Split(':');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;
            var type = EcgPointTypes.FromToken(parts[1]);
            if (type is null) continue;
            outList.Add(new SignificantPoint(index, type.Value));
        }
        return outList;
    }

    private static string? Get(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    private static int? ToIntOrNull(string? s) =>
        s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
