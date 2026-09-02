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

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public RhythmViewModel(PathologyRepository repository, DataSourcePrefs? prefs = null)
    {
        _repository = repository;
        _prefs = prefs;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _repository.ManifestChanged += (_, _) => RunOnUi(() => _ = LoadManifestAsync());
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

            // Restore last selected rhythm or update existing selection
            if (SelectedRhythm is { } current && _allRhythms.Any(r => r.Id == current.Id))
            {
                SelectRhythm(current.Id, persist: true);
            }
            else if (_prefs?.LastRhythmId is { } lastId && _allRhythms.Any(r => r.Id == lastId))
            {
                SelectRhythm(lastId, persist: true);
            }
            else if (_allRhythms.Count > 0)
            {
                SelectRhythm(_allRhythms[0].Id, persist: true);
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

    public void SelectRhythm(string id, bool persist = true)
    {
        var entry = _allRhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null)
        {
            SelectedRhythm = null;
            Waveforms = new Dictionary<Lead, Points>();
            SignificantPoints = Array.Empty<SignificantPoint>();
            Tips = Array.Empty<TipOverlay>();
            TipComments = Array.Empty<string>();
            Description = null;
            return;
        }

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
        SelectedRhythm = null;
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
        SelectedRhythm = null;
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
