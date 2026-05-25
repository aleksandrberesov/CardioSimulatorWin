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

    [ObservableProperty]
    private int _selectedIndex;

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
        SelectedIndex = 0;
        IsMetadataDirty = false;
    }

    public void SelectLead(Lead lead)
    {
        FocusedLead = lead;
        SelectedIndex = 0;
    }

    public void SelectIndex(int index)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(FocusedLead, out var stream)) return;
        if (index >= 0 && index < stream.Samples.Length) SelectedIndex = index;
    }

    public void SelectNext()
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(FocusedLead, out var stream)) return;
        if (SelectedIndex < stream.Samples.Length - 1) SelectedIndex++;
    }

    public void SelectPrevious()
    {
        if (SelectedIndex > 0) SelectedIndex--;
    }

    /// <summary>Cycles the selection through the points of the given type (mirrors Android).</summary>
    public void SelectSignificantPoint(EcgPointType type)
    {
        var file = TargetFile;
        if (file is null) return;
        var pointsOfType = file.SignificantPoints
            .Where(p => p.Type == type)
            .OrderBy(p => p.Index)
            .ToList();
        if (pointsOfType.Count == 0) return;
        var next = pointsOfType.FirstOrDefault(p => p.Index > SelectedIndex);
        SelectedIndex = next?.Index ?? pointsOfType[0].Index;
    }

    public void MoveSelectedUp()
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(FocusedLead, out var stream)) return;
        var index = SelectedIndex;
        if (index >= 0 && index < stream.Samples.Length)
            SetSample(FocusedLead, index, stream.Samples[index] + 1);
    }

    public void MoveSelectedDown()
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(FocusedLead, out var stream)) return;
        var index = SelectedIndex;
        if (index >= 0 && index < stream.Samples.Length)
            SetSample(FocusedLead, index, stream.Samples[index] - 1);
    }

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

    public void ToggleSignificantPoint(Lead lead, int index, EcgPointType type)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (index < 0 || index >= stream.Samples.Length) return;

        var points = file.SignificantPoints.ToList();
        var existing = points.FirstOrDefault(p => p.Index == index && p.Type == type);
        if (existing is not null)
        {
            points.Remove(existing);
        }
        else
        {
            points.RemoveAll(p => p.Index == index);
            points.Add(new SignificantPoint(index, type));
        }

        TargetFile = file with { SignificantPoints = points };
        IsMetadataDirty = true;
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
