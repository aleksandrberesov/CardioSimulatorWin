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

    [ObservableProperty]
    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();

    [ObservableProperty]
    private PathologyEntry? _selectedRhythm;

    [ObservableProperty]
    private IReadOnlyDictionary<Lead, Points> _waveforms = new Dictionary<Lead, Points>();    

    [ObservableProperty]
    private IReadOnlyList<SignificantPoint> _significantPoints = Array.Empty<SignificantPoint>();

    public RhythmViewModel(PathologyRepository repository, DataSourcePrefs? prefs = null)
    {
        _repository = repository;
        _prefs = prefs;
        _repository.ManifestChanged += (_, _) => _ = LoadManifestAsync();
    }

    public async Task LoadManifestAsync()
    {
        var entries = _repository.Pathologies();
        Rhythms = entries;

        // Enrichment: if manifest entries lack Russian names, peek-read them from the .dat files.
        if (entries.Any(e => string.IsNullOrWhiteSpace(e.NameRu)))
        {
            var enriched = await Task.Run(() => entries.Select(entry =>
            {
                if (!string.IsNullOrWhiteSpace(entry.NameRu)) return entry;
                var file = _repository.ReadPathology(entry.Id);
                return file?.NameRu is { } ru ? entry with { NameRu = ru } : entry;
            }).ToList());
            Rhythms = enriched;
        }

        // Restore last selected rhythm or update existing selection
        if (_prefs?.LastRhythmId is { } lastId && SelectedRhythm is null)
        {
            SelectRhythm(lastId, persist: false);
        }
        else if (SelectedRhythm is { } current)
        {
            SelectRhythm(current.Id);
        }
    }

    public void SelectRhythm(string id, bool persist = true)
    {
        var entry = Rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return;
        SelectedRhythm = entry;

        if (persist && _prefs is not null)
        {
            _prefs.LastRhythmId = id;
        }

        SignificantPoints = _repository.ReadPathology(id)?.SignificantPoints ?? Array.Empty<SignificantPoint>();

        var leadOrder = _repository.Manifest()?.LeadOrder ?? Leads.All;
        var map = new Dictionary<Lead, Points>();
        foreach (var lead in leadOrder)
        {
            var points = _repository.LeadWaveform(id, lead);
            if (points is not null) map[lead] = points;
        }
        Waveforms = map;
    }

    public void Refresh()
    {
        if (SelectedRhythm is { } r) SelectRhythm(r.Id);
    }
}
