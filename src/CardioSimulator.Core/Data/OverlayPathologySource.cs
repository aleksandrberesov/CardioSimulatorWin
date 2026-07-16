using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// A writable pathology source that layers an encrypted <see cref="WritableEncryptedOverlay"/> over
/// a read-only base (the bundled content pack). This is what lets the Full-edition constructor
/// create/edit/duplicate/delete pathologies on a protected pack build without ever writing the
/// bundle — or content derived from it — as plaintext on disk.
///
/// <para>Copy-on-write semantics: reads resolve overlay-first then base; writes go only to the
/// overlay (editing a bundled pathology creates an overlay override, the pack stays byte-for-byte
/// intact); deleting a bundled pathology records a tombstone so it disappears from every list while
/// the pack is untouched. The overlay stores per-pathology <c>&lt;id&gt;.dat</c>, a delta
/// <c>manifest.txt</c>, a <c>deleted.txt</c> tombstone list, and (optionally) <c>groups.txt</c> —
/// all inside one AES-256-GCM file.</para>
/// </summary>
public sealed class OverlayPathologySource : IWritablePathologySource, IContentPackExportable
{
    private const string ManifestName = "manifest.txt";
    private const string TombstonesName = "deleted.txt";
    private const string GroupsName = "groups.txt";
    private const int DefaultBaseline = 1024;

    private readonly IPathologySource _base;
    private readonly WritableEncryptedOverlay _overlay;

    public OverlayPathologySource(IPathologySource baseSource, WritableEncryptedOverlay overlay)
    {
        _base = baseSource;
        _overlay = overlay;
    }

    // ── overlay bookkeeping ─────────────────────────────────────────────────

    private HashSet<string> LoadTombstones()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_overlay.ReadText(TombstonesName) is { } text)
        {
            foreach (var line in text.Split('\n'))
            {
                var id = line.Trim();
                if (id.Length > 0) set.Add(id);
            }
        }
        return set;
    }

    private void SaveTombstones(HashSet<string> t) => _overlay.PutText(TombstonesName, string.Join('\n', t));

    private List<PathologyEntry> LoadOverlayEntries()
    {
        if (_overlay.ReadText(ManifestName) is { } text)
        {
            try { return PathologyParser.ParseManifest(text).Entries.ToList(); }
            catch { /* fall through to empty */ }
        }
        return new List<PathologyEntry>();
    }

    private void SaveOverlayEntries(IReadOnlyList<PathologyEntry> entries)
    {
        var baseM = _base.ReadManifest();
        var manifest = new PathologyManifest(
            baseM?.Version ?? PathologyManifest.SupportedVersion,
            baseM?.Baseline ?? DefaultBaseline,
            baseM?.LeadOrder ?? Leads.All,
            entries);
        _overlay.PutText(ManifestName, PathologyParser.SerializeManifest(manifest));
    }

    // ── reads (overlay over base) ───────────────────────────────────────────

    public PathologyManifest? ReadManifest()
    {
        var baseM = _base.ReadManifest();
        var overlayEntries = LoadOverlayEntries();
        var tomb = LoadTombstones();
        var overlayById = overlayEntries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        if (baseM is null)
        {
            var only = overlayEntries.Where(e => !tomb.Contains(e.Id)).ToList();
            return only.Count == 0
                ? null
                : new PathologyManifest(PathologyManifest.SupportedVersion, DefaultBaseline, Leads.All, only);
        }

        var merged = new List<PathologyEntry>();
        var baseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in baseM.Entries)
        {
            baseIds.Add(e.Id);
            if (tomb.Contains(e.Id)) continue;
            merged.Add(overlayById.TryGetValue(e.Id, out var oe) ? oe : e);
        }
        foreach (var oe in overlayEntries)
        {
            if (!baseIds.Contains(oe.Id) && !tomb.Contains(oe.Id)) merged.Add(oe);
        }
        // Preserve the base header (Version/Baseline/LeadOrder — LeadWaveform depends on these).
        return baseM with { Entries = merged };
    }

    public PathologyFile? ReadPathology(string id)
    {
        if (LoadTombstones().Contains(id)) return null;
        // Read raw bytes so the parser auto-detects delta-binary (CSD1) vs text. Overlays written by
        // this build are binary; reading through the sniffing entry point keeps text overlays from
        // older builds loading unchanged.
        if (_overlay.Read($"{id}.dat") is { } bytes)
        {
            try { return PathologyParser.ParsePathology(bytes); }
            catch { return null; }
        }
        return _base.ReadPathology(id);
    }

    public IReadOnlyList<string> ListPathologies()
    {
        var tomb = LoadTombstones();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _base.ListPathologies())
        {
            if (!tomb.Contains(id)) ids.Add(id);
        }
        foreach (var name in _overlay.EntryPaths)
        {
            if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                var id = name[..^4];
                if (!tomb.Contains(id)) ids.Add(id);
            }
        }
        return ids.ToList();
    }

    // ── writes (overlay only) ───────────────────────────────────────────────

    public bool WritePathology(PathologyFile file, IReadOnlyList<Lead>? leadOrder = null)
    {
        try
        {
            var order = leadOrder ?? _base.ReadManifest()?.LeadOrder ?? Leads.All;
            // Binary (CSD1): the overlay lives inside a pack, and packs are binary-only.
            _overlay.Put($"{file.Id}.dat", PathologyParser.SerializePathologyBytes(file, order));

            // Upsert the overlay's delta manifest entry (mirrors FilePathologySource sync fields).
            var entries = LoadOverlayEntries();
            var entry = new PathologyEntry(
                file.Id, file.TitleEn, file.NameRu, file.Leads.Count, $"{file.Id}.dat",
                file.Group, file.ClinicalCase, file.Number);
            var idx = entries.FindIndex(e => e.Id == file.Id);
            if (idx >= 0) entries[idx] = entry; else entries.Add(entry);
            SaveOverlayEntries(entries);

            // Writing a previously-deleted id resurrects it.
            var tomb = LoadTombstones();
            if (tomb.Remove(file.Id)) SaveTombstones(tomb);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeletePathology(string id)
    {
        try
        {
            _overlay.Delete($"{id}.dat");
            var entries = LoadOverlayEntries();
            if (entries.RemoveAll(e => e.Id == id) > 0) SaveOverlayEntries(entries);

            // A bundled (base) pathology can't be removed from the read-only pack → tombstone it.
            if (BaseHas(id))
            {
                var tomb = LoadTombstones();
                if (tomb.Add(id)) SaveTombstones(tomb);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? CreatePathology(string titleEn, string? nameRu, int sampleCount, int baseline)
    {
        try
        {
            var newId = GenerateUniqueId(SanitizeId(titleEn));
            var order = _base.ReadManifest()?.LeadOrder ?? Leads.All;
            var flat = new int[sampleCount];
            Array.Fill(flat, baseline);
            var leads = order.ToDictionary(l => l, l => new LeadStream(l, (int[])flat.Clone()));
            var file = new PathologyFile(newId, titleEn, nameRu, leads);
            return WritePathology(file, order) ? newId : null;
        }
        catch
        {
            return null;
        }
    }

    public string? ImportPathology(PathologyFile file)
    {
        try
        {
            var seed = string.IsNullOrWhiteSpace(file.Id) ? file.TitleEn : file.Id;
            var newId = GenerateUniqueId(SanitizeId(seed));
            return WritePathology(file with { Id = newId }) ? newId : null;
        }
        catch
        {
            return null;
        }
    }

    public string? DuplicatePathology(string id)
    {
        try
        {
            // Reads through the MERGE, so a bundled pathology can be duplicated; the (decrypted)
            // copy is written back into the ENCRYPTED overlay, never plaintext.
            var file = ReadPathology(id);
            if (file is null) return null;
            var newId = GenerateUniqueId(id + "_copy");
            return WritePathology(file with { Id = newId }) ? newId : null;
        }
        catch
        {
            return null;
        }
    }

    // ── groups (overlay-writable) ───────────────────────────────────────────

    /// <summary>The overlay's groups.txt if the instructor added groups, else the base pack's.</summary>
    public string? ReadGroupsText() =>
        _overlay.ReadText(GroupsName) ?? (_base as EncryptedPathologySource)?.ReadGroupsText();

    /// <summary>Persists the full rhythm-group catalog into the encrypted overlay.</summary>
    public void WriteGroupsText(string text) => _overlay.PutText(GroupsName, text);

    // ── export (merged, for a distributable pak) ────────────────────────────

    public IEnumerable<KeyValuePair<string, byte[]>> ExportEntries()
    {
        var manifest = ReadManifest();
        if (manifest is not null)
            yield return Bytes("manifest.txt", PathologyParser.SerializeManifest(manifest));

        var order = manifest?.LeadOrder ?? Leads.All;
        foreach (var id in ListPathologies())
        {
            var file = ReadPathology(id);
            if (file is not null)
                yield return new KeyValuePair<string, byte[]>(
                    $"{id}.dat", PathologyParser.SerializePathologyBytes(file, order));
        }

        if (ReadGroupsText() is { } groups)
            yield return Bytes("groups.txt", groups);
    }

    private static KeyValuePair<string, byte[]> Bytes(string name, string text) =>
        new(name, System.Text.Encoding.UTF8.GetBytes(text));

    // ── id allocation across the merged set ─────────────────────────────────

    private bool BaseHas(string id) => _base.ListPathologies().Contains(id);

    private bool IdTaken(string id) =>
        LoadTombstones().Contains(id) || _overlay.Exists($"{id}.dat") || BaseHas(id);

    private string GenerateUniqueId(string baseId)
    {
        var id = baseId;
        var suffix = 1;
        while (IdTaken(id))
        {
            suffix++;
            id = $"{baseId}{suffix}";
        }
        return id;
    }

    private static string SanitizeId(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var id = new string(chars).Trim('_');
        return string.IsNullOrEmpty(id) ? "new_pathology" : id;
    }
}
