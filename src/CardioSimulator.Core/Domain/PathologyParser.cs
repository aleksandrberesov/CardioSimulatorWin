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
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ─── delta-binary (.dat) format ─────────────────────────────────────
    //
    // A compact, loss-free binary encoding of a pathology used only inside the distributed
    // encrypted packs (loose files on disk stay plain text). The waveform dominates a .dat, so
    // samples are stored as delta-encoded 16-bit integers (each ~0 for a smooth trace) which zips
    // far smaller than the comma-separated decimal text. All the small metadata (title, name,
    // markers, tips, elements, …) is carried verbatim by reusing the existing text sub-encodings,
    // so binary round-trips exactly as text does and Android needs to mirror only the framing below.
    //
    // Layout (little-endian; a "string" is int32 length (-1 = null) + that many UTF-8 bytes):
    //   [4]      magic 'C','S','D','1'
    //   string   header text block (SerializeHeader output — everything before the lead blocks)
    //   int32    lead count N
    //   N ×      { uint8 lead index; string elements text (nullable); int32 sampleCount;
    //             int16[sampleCount] delta samples }
    // No version field: the 4-byte magic is the sole discriminator. If the framing ever needs to
    // change incompatibly, bump the magic (e.g. CSD2) rather than adding an in-band version.
    private static readonly byte[] BinaryMagic = { (byte)'C', (byte)'S', (byte)'D', (byte)'1' };

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
                FileName: $"{id}.dat",
                Group: Get(fields, "group"),
                ClinicalCase: Get(fields, "clinical_case"),
                Number: ToIntOrNull(Get(fields, "number")),
                Acronym: Get(fields, "acronym")));
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
            if (e.Number is { } number)
            {
                sb.Append(";number:").Append(number.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrWhiteSpace(e.NameRu))
            {
                sb.Append(";name:").Append(e.NameRu);
            }
            if (!string.IsNullOrWhiteSpace(e.Group))
            {
                sb.Append(";group:").Append(e.Group);
            }
            if (!string.IsNullOrWhiteSpace(e.ClinicalCase))
            {
                sb.Append(";clinical_case:").Append(e.ClinicalCase);
            }
            if (!string.IsNullOrWhiteSpace(e.Acronym))
            {
                sb.Append(";acronym:").Append(e.Acronym);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ─── <pathology>.dat ────────────────────────────────────────────────

    /// <summary>
    /// Parses a <c>.dat</c> from raw bytes, auto-detecting the format: a <c>CSD1</c> magic header
    /// selects the delta-binary decoder, anything else is treated as UTF-8 text (BOM tolerated).
    /// This is the entry point the read paths use so the same pack can mix binary and text entries.
    /// </summary>
    public static PathologyFile ParsePathology(byte[] bytes)
    {
        if (bytes.Length == 0) throw new PathologyFormatException("pathology: empty file");
        return HasBinaryMagic(bytes)
            ? ParsePathologyBinary(bytes)
            : ParsePathologyText(DecodeUtf8(bytes));
    }

    /// <summary>Parses a <c>.dat</c> from text. Forwards to the byte[] overload so the format
    /// detection lives in one place (a real text <c>.dat</c> starts with <c>pathology:</c>, never the
    /// binary magic).</summary>
    public static PathologyFile ParsePathology(string text) =>
        ParsePathology(Utf8.GetBytes(text));

    private static PathologyFile ParsePathologyText(string text)
    {
        var blocks = SplitBlocks(text);
        if (blocks.Count == 0) throw new PathologyFormatException("pathology: empty file");

        var header = blocks[0];
        var id = Get(header, "pathology")
            ?? throw new PathologyFormatException("pathology: missing 'pathology'");

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
            var elements = ParseElements(Get(block, "elements"));
            leads[lead] = new LeadStream(lead, samples, elements);
        }
        return BuildFromHeader(header, leads);
    }

    private static PathologyFile ParsePathologyBinary(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms);
        ms.Position = BinaryMagic.Length; // magic already validated by the caller

        var headerText = ReadNullableString(r)
            ?? throw new PathologyFormatException("pathology: binary is missing its header block");
        var headerBlocks = SplitBlocks(headerText);
        var header = headerBlocks.Count > 0 ? headerBlocks[0] : new Dictionary<string, string>();
        var id = Get(header, "pathology")
            ?? throw new PathologyFormatException("pathology: missing 'pathology'");

        var leadCount = r.ReadInt32();
        var leads = new Dictionary<Lead, LeadStream>();
        for (var i = 0; i < leadCount; i++)
        {
            var leadIndex = r.ReadByte();
            if (leadIndex >= Leads.All.Count)
            {
                throw new PathologyFormatException($"pathology[{id}]: binary lead index {leadIndex} out of range");
            }
            var lead = (Lead)leadIndex;
            var elementsText = ReadNullableString(r);
            var sampleCount = r.ReadInt32();
            if (sampleCount < 0)
            {
                throw new PathologyFormatException($"pathology[{id}]: binary lead {lead} has negative sample count");
            }
            var samples = ReadDeltaSamples(r, sampleCount);
            var elements = ParseElements(elementsText);
            leads[lead] = new LeadStream(lead, samples, elements);
        }
        return BuildFromHeader(header, leads);
    }

    /// <summary>Builds a <see cref="PathologyFile"/> from a parsed header block and its leads.
    /// Shared by the text and binary decoders so header semantics live in one place.</summary>
    private static PathologyFile BuildFromHeader(
        IReadOnlyDictionary<string, string> header,
        IReadOnlyDictionary<Lead, LeadStream> leads)
    {
        var id = Get(header, "pathology")
            ?? throw new PathologyFormatException("pathology: missing 'pathology'");
        var title = Get(header, "title") ?? string.Empty;
        var name = Get(header, "name");
        var group = Get(header, "group");
        var clinicalCase = Get(header, "clinical_case");
        var number = ToIntOrNull(Get(header, "number")?.Trim());
        var acronym = Get(header, "acronym");
        var description = Get(header, "description")?.Replace("\\n", "\n");
        var markers = ParseMarkers(Get(header, "markers"));
        var tips = ParseTips(Get(header, "tips"));
        var tipComments = ParseTipComments(Get(header, "tip_notes"));
        return new PathologyFile(id, title, name, leads) { SignificantPoints = markers, Group = group, ClinicalCase = clinicalCase, Number = number, Acronym = acronym, Description = description, Tips = tips, TipComments = tipComments };
    }

    public static string SerializePathology(PathologyFile file, IReadOnlyList<Lead> leadOrder)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, file);
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
            if (stream.Elements.Count > 0)
            {
                sb.Append("elements:").Append(SerializeElements(stream.Elements)).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serializes a pathology into the <c>CSD1</c> delta-binary payload (see the format note at the
    /// top of this class). Metadata is carried by the shared text header encoding; only the waveform
    /// samples are delta-compressed. Throws if a sample falls outside the 16-bit range.
    /// </summary>
    public static byte[] SerializePathologyBytes(PathologyFile file, IReadOnlyList<Lead> leadOrder)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Utf8, leaveOpen: true))
        {
            w.Write(BinaryMagic);
            WriteNullableString(w, SerializeHeader(file));

            var orderedLeads = leadOrder.Where(file.Leads.ContainsKey).ToList();
            w.Write(orderedLeads.Count);
            foreach (var lead in orderedLeads)
            {
                var stream = file.Leads[lead];
                w.Write((byte)(int)lead);
                WriteNullableString(w, stream.Elements.Count > 0 ? SerializeElements(stream.Elements) : null);
                w.Write(stream.Samples.Length);
                WriteDeltaSamples(w, stream.Samples, file.Id);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Serializes just the header block (everything the text format writes before the first
    /// lead block). Used verbatim inside the binary payload.</summary>
    private static string SerializeHeader(PathologyFile file)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, file);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, PathologyFile file)
    {
        sb.Append("pathology:").Append(file.Id).Append('\n');
        sb.Append("title:").Append(file.TitleEn).Append('\n');
        if (file.Number is { } number)
        {
            sb.Append("number:").Append(number.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        sb.Append("name:").Append(file.NameRu ?? string.Empty).Append('\n');
        if (!string.IsNullOrWhiteSpace(file.Group))
        {
            sb.Append("group:").Append(file.Group).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(file.Acronym))
        {
            sb.Append("acronym:").Append(file.Acronym).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(file.ClinicalCase))
        {
            sb.Append("clinical_case:").Append(file.ClinicalCase).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(file.Description))
        {
            sb.Append("description:").Append(file.Description.Replace("\r\n", "\n").Replace("\n", "\\n")).Append('\n');
        }
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
        if (file.Tips.Count > 0)
        {
            sb.Append("tips:").Append(SerializeTips(file.Tips)).Append('\n');
        }
        if (file.TipComments.Count > 0)
        {
            sb.Append("tip_notes:").Append(string.Join("~", file.TipComments.Select(EscapeTipText))).Append('\n');
        }
    }

    // ─── binary primitives ──────────────────────────────────────────────

    /// <summary>True if <paramref name="bytes"/> is a <c>CSD1</c> delta-binary <c>.dat</c> rather than
    /// text. Public so callers that convert a pack back to the text format can tell which entries need
    /// decoding without re-declaring the magic and letting the two copies drift.</summary>
    public static bool HasBinaryMagic(byte[] bytes) =>
        bytes.Length >= BinaryMagic.Length + 1
        && bytes[0] == BinaryMagic[0] && bytes[1] == BinaryMagic[1]
        && bytes[2] == BinaryMagic[2] && bytes[3] == BinaryMagic[3];

    private static string DecodeUtf8(byte[] bytes)
    {
        // Tolerate a UTF-8 BOM so loose files saved by other editors still parse.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8.GetString(bytes, 3, bytes.Length - 3);
        }
        return Utf8.GetString(bytes);
    }

    private static void WriteNullableString(BinaryWriter w, string? value)
    {
        if (value is null) { w.Write(-1); return; }
        var bytes = Utf8.GetBytes(value);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static string? ReadNullableString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length < 0) return null;
        var bytes = r.ReadBytes(length);
        return Utf8.GetString(bytes);
    }

    /// <summary>
    /// Writes samples as 16-bit deltas from the previous sample. Reconstruction relies on two's
    /// complement wrap-around, so any sample that itself fits in <see cref="short"/> decodes exactly
    /// even when a jump between two samples would overflow a 16-bit delta.
    /// </summary>
    private static void WriteDeltaSamples(BinaryWriter w, int[] samples, string id)
    {
        var prev = 0;
        foreach (var sample in samples)
        {
            if (sample < short.MinValue || sample > short.MaxValue)
            {
                throw new PathologyFormatException(
                    $"pathology[{id}]: sample {sample} is outside the 16-bit range; cannot delta-encode");
            }
            w.Write((short)(sample - prev));
            prev = sample;
        }
    }

    private static int[] ReadDeltaSamples(BinaryReader r, int count)
    {
        var samples = new int[count];
        var prev = 0;
        for (var i = 0; i < count; i++)
        {
            var delta = r.ReadInt16();
            var value = (short)(prev + delta);
            samples[i] = value;
            prev = value;
        }
        return samples;
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

    /// <summary>Serializes a lead's placed elements as <c>Type:start:length:amp,…</c>.</summary>
    private static string SerializeElements(IReadOnlyList<EcgElementInstance> elements)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < elements.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = elements[i];
            sb.Append(e.Type.ToString()).Append(':')
              .Append(e.StartIndex.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(e.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(e.AmplitudeMv.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses the <c>elements:</c> field (<c>Type:start:length:amp,…</c>). Skips tokens with a bad
    /// shape, an unknown type, or non-numeric fields — same tolerance as <see cref="ParseMarkers"/>.
    /// </summary>
    private static IReadOnlyList<EcgElementInstance> ParseElements(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<EcgElementInstance>();
        var outList = new List<EcgElementInstance>();
        foreach (var token in field.Split(','))
        {
            var parts = token.Split(':');
            if (parts.Length != 4) continue;
            if (!Enum.TryParse<EcgElement>(parts[0].Trim(), out var type)) continue;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) continue;
            if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)) continue;
            if (!float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amp)) continue;
            outList.Add(new EcgElementInstance(type, start, length, amp));
        }
        return outList;
    }

    // ─── tips (authored annotation overlays) ─────────────────────────────
    //
    // One header line: overlays separated by '~', fields within an overlay by '|':
    //   Kind|EndCap|Lead|Text|s:a;s:a;…
    // Text is percent-escaped (%, |, ~, CR, LF) so it can't collide with the delimiters or wrap
    // the single header line. Lead is a token or empty. Points are "sample:adc" pairs (invariant
    // floats) joined by ';'. Parsing is tolerant: malformed overlays/points are skipped.

    /// <summary>Serializes authored tip overlays into the single <c>tips:</c> header value.</summary>
    private static string SerializeTips(IReadOnlyList<TipOverlay> tips) => TipOverlaySerializer.Serialize(tips);

    /// <summary>Parses the <c>tips:</c> header value. Skips overlays with an unknown kind or a bad shape.</summary>
    private static IReadOnlyList<TipOverlay> ParseTips(string? field) => TipOverlaySerializer.Parse(field);

    /// <summary>Parses the <c>tip_notes:</c> header value (percent-escaped comments, '~'-separated).</summary>
    private static IReadOnlyList<string> ParseTipComments(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return Array.Empty<string>();
        return field.Split('~').Select(UnescapeTipText).ToList();
    }

    private static string EscapeTipText(string? text) => TipOverlaySerializer.EscapeText(text);

    private static string UnescapeTipText(string text) => TipOverlaySerializer.UnescapeText(text);

    private static string? Get(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    private static int? ToIntOrNull(string? s) =>
        s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
