using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Holds the current <see cref="IPathologySource"/>, caches the manifest, lazily
/// reads pathology files on demand, and exposes baseline-zeroed <see cref="Points"/>
/// per (pathology id, <see cref="Lead"/>).
/// </summary>
public sealed class PathologyRepository
{
    private const int DefaultBaseline = 1024;

    private volatile PathologyManifest? _manifest;
    private IPathologySource _source;

    // Most-recently-read pathology. LeadWaveform reads the whole file per lead, so drawing one
    // 12-lead rhythm asked the source for the same .dat 13 times. That was invisible when the source
    // was a fully in-RAM buffer, but a lazily decrypted pack would re-decrypt and re-inflate it every
    // time. One slot is enough: the access pattern is a burst of reads for the selected pathology.
    private readonly object _memoGate = new();
    private string? _memoId;
    private PathologyFile? _memoFile;

    /// <summary>Raised when the manifest is loaded or updated (e.g. after a write).</summary>
    public event EventHandler? ManifestChanged;

    public PathologyRepository(IPathologySource source)
    {
        _source = source;
    }

    public void SetSource(IPathologySource newSource)
    {
        _source = newSource;
        _manifest = null;
        InvalidateMemo();
        ManifestChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateMemo()
    {
        lock (_memoGate)
        {
            _memoId = null;
            _memoFile = null;
        }
    }

    /// <summary>The active source (e.g. for export re-packaging via <see cref="IContentPackExportable"/>).</summary>
    public IPathologySource Source => _source;

    public bool LoadManifest()
    {
        var m = _source.ReadManifest();
        _manifest = m;
        // Every mutating path (write/delete/duplicate/import/create) reloads the manifest, so
        // invalidating here is the single choke point that keeps the read memo from serving a stale
        // copy of a pathology the constructor just edited.
        InvalidateMemo();
        ManifestChanged?.Invoke(this, EventArgs.Empty);
        return m is not null;
    }

    public PathologyManifest? Manifest() => _manifest;

    public IReadOnlyList<PathologyEntry> Pathologies() =>
        _manifest?.Entries
            .OrderBy(e => e.TitleEn.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList()
        ?? (IReadOnlyList<PathologyEntry>)Array.Empty<PathologyEntry>();

    public PathologyFile? ReadPathology(string id)
    {
        lock (_memoGate)
        {
            if (_memoId == id && _memoFile is not null) return _memoFile;
        }

        // Read outside the lock: decrypt + inflate + parse is slow, and holding the memo lock across
        // it would serialise unrelated callers. A concurrent duplicate read is harmless (same result,
        // wasted work only), which is a better trade than a global read lock.
        var file = _source.ReadPathology(id);

        lock (_memoGate)
        {
            _memoId = file is null ? null : id;
            _memoFile = file;
        }
        return file;
    }

    /// <summary>
    /// Persists <paramref name="file"/> back to the source. Only supported if the
    /// current source is a <see cref="FilePathologySource"/>.
    /// </summary>
    public bool WritePathology(PathologyFile file)
    {
        if (_source is IWritablePathologySource s && s.WritePathology(file, _manifest?.LeadOrder))
        {
            // Reload manifest to pick up title changes (Android parity)
            LoadManifest();
            return true;
        }
        return false;
    }

    /// <summary>Deletes a pathology (file + manifest entry) via the file-backed source.</summary>
    public bool DeletePathology(string id)
    {
        if (_source is not IWritablePathologySource s) return false;
        var ok = s.DeletePathology(id);
        if (ok) LoadManifest();
        return ok;
    }

    /// <summary>
    /// Duplicates a pathology under a fresh id (file + manifest entry). Returns the new id or null.
    /// </summary>
    public string? DuplicatePathology(string id)
    {
        if (_source is not IWritablePathologySource s) return null;
        var newId = s.DuplicatePathology(id);
        if (newId is not null) LoadManifest();
        return newId;
    }

    /// <summary>
    /// Imports an externally produced pathology (file + manifest entry) under a fresh id.
    /// Returns the new id or null. Only supported on a <see cref="FilePathologySource"/>.
    /// </summary>
    public string? ImportPathology(PathologyFile file)
    {
        if (_source is not IWritablePathologySource s) return null;
        var newId = s.ImportPathology(file);
        if (newId is not null) LoadManifest();
        return newId;
    }

    /// <summary>Creates a new blank pathology (file + manifest entry). Returns the new id or null.</summary>
    public string? CreatePathology(string titleEn, string? nameRu)
    {
        if (_source is not IWritablePathologySource s) return null;
        var baseline = _manifest?.Baseline ?? DefaultBaseline;
        var newId = s.CreatePathology(titleEn, nameRu, 501, baseline);
        if (newId is not null) LoadManifest();
        return newId;
    }

    /// <summary>
    /// Returns the baseline-zeroed <see cref="Points"/> for one lead of one
    /// pathology, synthesizing the lead via <see cref="DerivedLeads"/> if the file
    /// does not ship it. Returns null when neither a direct lead nor a derivable
    /// basis pair is available.
    /// </summary>
    public Points? LeadWaveform(string id, Lead lead)
    {
        var file = ReadPathology(id);
        if (file is null) return null;
        var baseline = _manifest?.Baseline ?? DefaultBaseline;

        if (file.Leads.TryGetValue(lead, out var stream))
        {
            return new Points(ZeroSamples(stream.Samples, baseline));
        }

        var synthesized = Synthesize(lead, file.Leads, baseline);
        return synthesized is null ? null : new Points(synthesized);
    }

    private static float[] ZeroSamples(int[] samples, int baseline)
    {
        var outArr = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            outArr[i] = samples[i] - baseline;
        }
        return outArr;
    }

    private static IReadOnlyList<float>? Synthesize(
        Lead target, IReadOnlyDictionary<Lead, LeadStream> leads, int baseline)
    {
        IReadOnlyList<float>? Zeroed(Lead l) =>
            leads.TryGetValue(l, out var st) ? ZeroSamples(st.Samples, baseline) : null;      

        switch (target)
        {
            case Lead.III:
            case Lead.aVR:
            case Lead.aVL:
            case Lead.aVF:
            {
                var i = Zeroed(Lead.I);
                if (i is null) return null;
                var ii = Zeroed(Lead.II);
                if (ii is null) return null;
                var result = DerivedLeads.CombineIII_aVR_aVL_aVF(i, ii, target);
                return result.Count > 0 ? result : null;
            }
            case Lead.V1:
            case Lead.V3:
            case Lead.V4:
            case Lead.V5:
            {
                var v2 = Zeroed(Lead.V2);
                if (v2 is null) return null;
                var v6 = Zeroed(Lead.V6);
                if (v6 is null) return null;
                var result = DerivedLeads.CombineV1_V3_V4_V5(v2, v6, target);
                return result.Count > 0 ? result : null;
            }
            default:
                return null;
        }
    }
}
