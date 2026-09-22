using CardioSimulator.App.Data;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Exposes the manifest's pathology index and the baseline-zeroed waveforms for the
/// currently selected pathology. Faithful port of the Android <c>RhythmViewModel</c>.
/// </summary>
public partial class RhythmViewModel : ObservableObject
{
    private readonly PathologyRepository _repository;
    private readonly DataSourcePrefs? _prefs;

    /// <summary>Unfiltered pathology index; <see cref="Rhythms"/> is this filtered by course.</summary>
    private IReadOnlyList<PathologyEntry> _allRhythms = Array.Empty<PathologyEntry>();

    /// <summary>Active course filter (pathology ids); null = show all. Mirrors the Android
    /// course-aware filter on the Teaching rhythm list.</summary>
    private IReadOnlyList<string>? _courseFilter;

    [ObservableProperty]
    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();

    [ObservableProperty]
    private PathologyEntry? _selectedRhythm;

    [ObservableProperty]
    private IReadOnlyDictionary<Lead, Points> _waveforms = new Dictionary<Lead, Points>();

    [ObservableProperty]
    private IReadOnlyDictionary<int, Points> _comparisonWaveforms = new Dictionary<int, Points>();

    [ObservableProperty]
    private IReadOnlyList<SignificantPoint> _significantPoints = Array.Empty<SignificantPoint>();

    /// <summary>Authored annotation overlays for the selected pathology (rendered on the monitor).</summary>
    [ObservableProperty]
    private IReadOnlyList<TipOverlay> _tips = Array.Empty<TipOverlay>();

    /// <summary>Authored text comments/explanations for the selected pathology (the "Видим:" list).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _tipComments = Array.Empty<string>();

    [ObservableProperty]
    private string? _description;

    /// <summary>
    /// The rhythm the user picked that is still waiting for the TCP monitor server to be switched over (the monitor
    /// keeps drawing <see cref="SelectedRhythm"/> meanwhile — see <see cref="SelectRhythm"/>), or null. The host
    /// reports it on a reconnect, so a link that drops mid-switch is re-fed the rhythm the app is about to show
    /// rather than the one it is leaving.
    /// </summary>
    public PathologyEntry? PendingRhythm { get; private set; }

    /// <summary>
    /// Host hook that decides whether a user selection must wait. Returns a task to wait on (the app keeps
    /// drawing the current rhythm until it completes) or <c>null</c> to swap immediately — which is what an
    /// unset gate, a stopped monitor and a down link all mean. Set by <c>MainScreen</c>.
    /// </summary>
    public Func<PathologyEntry, Task?>? SelectionGate { get; set; }

    /// <summary>Last-wins guard: every selection takes the next token, and a pending commit is dropped when a
    /// newer selection has been made in the meantime.</summary>
    private int _pendingToken;

    /// <summary>Upper bound on how long a selection may keep showing the old rhythm. The handshake itself is
    /// bounded (a 4 s verdict timeout), but the samples that follow are not, and a stalled socket must never
    /// pin the monitor to a rhythm the user did not choose.</summary>
    private const int SwitchCommitTimeoutMs = 15000;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly EventHandler _onManifestChanged;

    public RhythmViewModel(PathologyRepository repository, DataSourcePrefs? prefs = null)
    {
        _repository = repository;
        _prefs = prefs;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _onManifestChanged = (_, _) => RunOnUi(() => _ = LoadManifestAsync());
        _repository.ManifestChanged += _onManifestChanged;
    }

    /// <summary>
    /// Retires this view-model when the host builds a fresh one (every mode switch and language change does).
    /// Without it the discarded instance stays subscribed to <see cref="PathologyRepository.ManifestChanged"/> for
    /// the life of the app, re-selects its old rhythm on every dataset reload and — through
    /// <see cref="SelectionGate"/> — pushes that stale rhythm to the TCP monitor server over the live one. Also
    /// drops a pending switch, so it can't land later and overwrite the persisted last-rhythm id.
    /// </summary>
    public void Detach()
    {
        _repository.ManifestChanged -= _onManifestChanged;
        SelectionGate = null;
        _pendingToken++;
        PendingRhythm = null;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher is { } d && !d.HasThreadAccess) d.TryEnqueue(() => action());
        else action();
    }

    public async Task LoadManifestAsync()
    {
        var entries = await Task.Run(() => _repository.Pathologies());

        // Enrichment: if manifest entries lack Russian names, peek-read them from the .dat files
        // or resolve composite titles via Taxonomy.Shared acronym translation.
        if (entries.Any(e => string.IsNullOrWhiteSpace(e.NameRu)))
        {
            entries = await Task.Run(() => entries.Select(entry =>
            {
                if (!string.IsNullOrWhiteSpace(entry.NameRu)) return entry;
                var ru = entry.ResolvedNameRu ?? _repository.ReadPathology(entry.Id)?.ResolvedNameRu;
                return ru is not null ? entry with { NameRu = ru } : entry;
            }).ToList());
        }

        RunOnUi(() =>
        {
            _allRhythms = entries;
            ApplyFilter();

            // Restore last selected rhythm or update existing selection. immediate: this is startup / a dataset
            // reload, not a user switch — it must not wait on the TCP monitor server (that would blank the
            // monitor on every mode rebuild and language change).
            if (SelectedRhythm is { } current && _allRhythms.Any(r => r.Id == current.Id))
            {
                SelectRhythm(current.Id, persist: true, immediate: true);
            }
            else if (_prefs?.LastRhythmId is { } lastId && _allRhythms.Any(r => r.Id == lastId))
            {
                SelectRhythm(lastId, persist: true, immediate: true);
            }
            else if (_allRhythms.Count > 0)
            {
                SelectRhythm(_allRhythms[0].Id, persist: true, immediate: true);
            }
            else
            {
                SelectedRhythm = null;
                Waveforms = new Dictionary<Lead, Points>();
                SignificantPoints = Array.Empty<SignificantPoint>();
                Tips = Array.Empty<TipOverlay>();
                TipComments = Array.Empty<string>();
                Description = null;
            }
        });
    }

    /// <summary>
    /// Filters the visible <see cref="Rhythms"/> to a course's pathologies (Android Teaching
    /// course filter). Pass null to clear the filter and show every rhythm.
    /// </summary>
    public void SetCourseFilter(IReadOnlyList<string>? pathologyIds)
    {
        if (ReferenceEquals(_courseFilter, pathologyIds)) return;
        if (_courseFilter is not null && pathologyIds is not null && _courseFilter.SequenceEqual(pathologyIds)) return;
        _courseFilter = pathologyIds;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_courseFilter is null)
        {
            Rhythms = _allRhythms;
        }
        else
        {
            var filterSet = new HashSet<string>(_courseFilter, StringComparer.OrdinalIgnoreCase);
            Rhythms = _allRhythms.Where(r => filterSet.Contains(r.Id)).ToList();
        }
    }

    /// <summary>
    /// Selects a rhythm. Normally this swaps everything the monitor shows at once, but when a
    /// <see cref="SelectionGate"/> is installed and asks to wait (the TCP monitor server is mid-switch — see
    /// <c>docs/tcp-protocol.md</c> §2), the whole swap is held back and the previously selected rhythm keeps
    /// drawing until the gate's task completes. <paramref name="immediate"/> opts a caller out of that wait for
    /// selections the user did not make — startup/manifest reloads and synthesized traces — which must never
    /// sit behind a socket. <paramref name="onCommitted"/> runs right after the selection is published, whether
    /// that is at once or when the server switch completes, for state that must change together with the rhythm
    /// (a lecture embed's lead layout); it is skipped if a newer selection supersedes this one.
    /// </summary>
    public void SelectRhythm(string id, bool persist = true, bool immediate = false, Action? onCommitted = null)
    {
        if (string.Equals(id, PathologyEntry.SyntheticAsystole.Id, StringComparison.OrdinalIgnoreCase))
        { ShowFlatline(); return; }
        if (string.Equals(id, PathologyEntry.SyntheticTorsades.Id, StringComparison.OrdinalIgnoreCase))
        { ShowTorsades(); return; }

        var entry = _allRhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null)
        {
            _pendingToken++; // an unresolvable id supersedes any pending switch
            PendingRhythm = null;
            SelectedRhythm = null;
            Waveforms = new Dictionary<Lead, Points>();
            SignificantPoints = Array.Empty<SignificantPoint>();
            Tips = Array.Empty<TipOverlay>();
            TipComments = Array.Empty<string>();
            Description = null;
            return;
        }

        var token = ++_pendingToken;
        // The gate runs for every selection — it is also what hands the rhythm to the server — but only a user
        // switch waits on the task it returns.
        var gate = SelectionGate?.Invoke(entry);
        if (immediate || gate is null)
        {
            CommitSelection(entry, persist);
            onCommitted?.Invoke();
            return;
        }

        // The server is being switched over. Keep the old rhythm on screen (and running) until it confirms,
        // then commit — unless a newer selection arrived meanwhile, or the wait ran long enough that stalling
        // the UI on the socket would be worse than showing what the user picked.
        PendingRhythm = entry;
        _ = AwaitGateAsync(gate, entry, persist, token, onCommitted);
    }

    private async Task AwaitGateAsync(Task gate, PathologyEntry entry, bool persist, int token, Action? onCommitted)
    {
        try
        {
            await Task.WhenAny(gate, Task.Delay(SwitchCommitTimeoutMs));
        }
        catch
        {
            // A faulted gate is still a settled one: draw the rhythm the user asked for.
        }

        RunOnUi(() =>
        {
            if (token != _pendingToken) return; // superseded by a newer selection — it owns the commit
            CommitSelection(entry, persist);
            onCommitted?.Invoke();
        });
    }

    /// <summary>Publishes the selection: every property the monitor reads changes in one synchronous burst, so
    /// markers, tips and samples are never paired across two different rhythms.</summary>
    private void CommitSelection(PathologyEntry entry, bool persist)
    {
        // Cleared here rather than at the end of the wait, so a selection that supersedes a pending one and
        // commits immediately (an internal one) also clears it.
        PendingRhythm = null;
        var id = entry.Id;
        SelectedRhythm = entry;

        if (persist && _prefs is not null)
        {
            _prefs.LastRhythmId = id;
        }

        var pathologyFile = _repository.ReadPathology(id);
        SignificantPoints = pathologyFile?.SignificantPoints ?? Array.Empty<SignificantPoint>();
        Tips = pathologyFile?.Tips ?? Array.Empty<TipOverlay>();
        TipComments = pathologyFile?.TipComments ?? Array.Empty<string>();
        Description = pathologyFile?.Description;

        var leadOrder = _repository.Manifest()?.LeadOrder ?? Leads.All;
        var map = new Dictionary<Lead, Points>();
        foreach (var lead in leadOrder)
        {
            var points = _repository.LeadWaveform(id, lead);        
            if (points is not null) map[lead] = points;
        }
        Waveforms = map;

        // Force notification so subscribers (e.g. MonitorView/EcgMonitorControl) re-render even if the ID matched.
        OnPropertyChanged(nameof(SelectedRhythm));
        OnPropertyChanged(nameof(Waveforms));
    }

    public void Refresh()
    {
        if (SelectedRhythm is { } r) SelectRhythm(r.Id);
    }

    /// <summary>Displays a synthesized isoelectric flat line across every lead (asystole) — the dataset has no
    /// asystole rhythm to select. Clears the selection/overlays; the monitor draws a flat trace. Used by
    /// Treatment mode (customer 28-08-2026).</summary>
    public void ShowFlatline()
    {
        _pendingToken++; // a synthesized trace supersedes any selection still waiting on the server
        PendingRhythm = null;
        SelectedRhythm = PathologyEntry.SyntheticAsystole;
        SignificantPoints = Array.Empty<SignificantPoint>();
        Tips = Array.Empty<TipOverlay>();
        TipComments = Array.Empty<string>();
        Description = null;

        const int sampleCount = 6000;                 // ~flat strip; exact rate is irrelevant for a flat line
        var flat = new Points(new float[sampleCount]); // baseline-zeroed floats → all zeros = the isoline
        var leadOrder = _repository.Manifest()?.LeadOrder ?? Leads.All;
        var map = new Dictionary<Lead, Points>();
        foreach (var lead in leadOrder) map[lead] = flat;
        Waveforms = map;

        OnPropertyChanged(nameof(SelectedRhythm));
        OnPropertyChanged(nameof(Waveforms));
    }

    /// <summary>
    /// Displays a procedurally synthesized torsades de pointes / polymorphic-VT trace. Used by the treatment
    /// panel for the <see cref="CardioSimulator.Core.Domain.Treatment.ClinicalRhythmState.Torsades"/> state
    /// when the pak has no authored <c>TDP</c> rhythm — a recognizable twisting-spindle waveform is far better
    /// than substituting a monomorphic VT. Like <see cref="ShowFlatline"/> this owns the trace synthetically
    /// (no selected pathology, no overlays).
    /// </summary>
    public void ShowTorsades()
    {
        _pendingToken++; // same as ShowFlatline: nothing pending may land on top of the synthesized trace
        PendingRhythm = null;
        SelectedRhythm = PathologyEntry.SyntheticTorsades;
        SignificantPoints = Array.Empty<SignificantPoint>();
        Tips = Array.Empty<TipOverlay>();
        TipComments = Array.Empty<string>();
        Description = null;

        const int sampleRateHz = 500;   // default monitor calibration (matches EcgCalibration)
        const int sampleCount = 6000;   // ~12 s strip (matches ShowFlatline)
        const float adcCountsPerMv = 1024f;
        var leadOrder = _repository.Manifest()?.LeadOrder ?? Leads.All;
        var synth = CardioSimulator.Core.Domain.Treatment.SyntheticEcg.Torsades(
            leadOrder, sampleRateHz, sampleCount, adcCountsPerMv);
        var map = new Dictionary<Lead, Points>();
        foreach (var lead in leadOrder)
            if (synth.TryGetValue(lead, out var samples)) map[lead] = new Points(samples);
        Waveforms = map;

        OnPropertyChanged(nameof(SelectedRhythm));
        OnPropertyChanged(nameof(Waveforms));
    }

    public async Task LoadComparisonWaveformAsync(int paneIndex, string pathologyId, Lead lead)
    {
        var points = await Task.Run(() => _repository.LeadWaveform(pathologyId, lead));
        if (points is not null)
        {
            var newMap = new Dictionary<int, Points>(ComparisonWaveforms) { [paneIndex] = points };
            ComparisonWaveforms = newMap;
        }
    }

    public void ClearComparisonWaveforms()
    {
        ComparisonWaveforms = new Dictionary<int, Points>();
    }

    public void RemoveComparisonWaveform(int paneIndex)
    {
        if (!ComparisonWaveforms.ContainsKey(paneIndex)) return;
        var newMap = new Dictionary<int, Points>(ComparisonWaveforms);
        newMap.Remove(paneIndex);
        ComparisonWaveforms = newMap;
    }
}
