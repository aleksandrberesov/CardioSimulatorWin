using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Edits a single <see cref="PathologyFile"/> at the raw-ADC level. Faithful port of the
/// Android <c>EditorViewModel</c>: pick a pathology, focus a lead, drag samples, revert a
/// lead, and save back through the repository (file-backed source only).
/// </summary>
public partial class EditorViewModel : ObservableObject
{
    private readonly PathologyRepository _repository;
    private readonly HashSet<Lead> _dirty = new();

    [ObservableProperty]
    private PathologyFile? _targetFile;

    [ObservableProperty]
    private Lead _focusedLead = Lead.II;

    [ObservableProperty]
    private IReadOnlyCollection<Lead> _dirtyLeads = Array.Empty<Lead>();

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isMetadataDirty;

    public EditorViewModel(PathologyRepository repository)
    {
        _repository = repository;
    }

    public void SelectPathology(string id)
    {
        TargetFile = _repository.ReadPathology(id);
        _dirty.Clear();
        DirtyLeads = Array.Empty<Lead>();
        FocusedLead = Lead.II;
        IsMetadataDirty = false;
    }

    public void SelectLead(Lead lead) => FocusedLead = lead;

    public void SetSample(Lead lead, int index, int adcValue)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (index < 0 || index >= stream.Samples.Length) return;
        if (stream.Samples[index] == adcValue) return;

        var newSamples = (int[])stream.Samples.Clone();
        newSamples[index] = adcValue;
        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };

        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    public void RevertLead(Lead lead)
    {
        var file = TargetFile;
        if (file is null) return;
        var original = _repository.ReadPathology(file.Id);
        if (original is null || !original.Leads.TryGetValue(lead, out var originalStream)) return;

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = originalStream };
        TargetFile = file with { Leads = newLeads };

        _dirty.Remove(lead);
        DirtyLeads = _dirty.ToArray();
    }

    public void Rename(string newName, Language lang)
    {
        var file = TargetFile;
        if (file is null) return;

        if (lang == Language.RU)
        {
            if (file.NameRu == newName) return;
            TargetFile = file with { NameRu = newName };
        }
        else
        {
            if (file.TitleEn == newName) return;
            TargetFile = file with { TitleEn = newName };
        }

        IsMetadataDirty = true;
    }

    public async Task SaveAsync()
    {
        var file = TargetFile;
        if (file is null || (_dirty.Count == 0 && !IsMetadataDirty)) return;

        IsSaving = true;
        try
        {
            await Task.Run(() => _repository.WritePathology(file));
            _dirty.Clear();
            DirtyLeads = Array.Empty<Lead>();
            IsMetadataDirty = false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
