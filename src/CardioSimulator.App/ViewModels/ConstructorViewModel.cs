using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Edits a single <see cref="PathologyFile"/> at the raw-ADC level. Faithful port of the
/// Android <c>ConstructorViewModel</c>: pick a pathology, focus a lead, drag samples, revert a
/// lead, and save back through the repository (file-backed source only).
/// </summary>
public partial class ConstructorViewModel : ObservableObject
{
    private readonly PathologyRepository _repository;
    private readonly HashSet<Lead> _dirty = new();

    private readonly Dictionary<Lead, Stack<int[]>> _undoStacks = new();
    private readonly Dictionary<Lead, Stack<int[]>> _redoStacks = new();

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

    [ObservableProperty]
    private ToolMode _toolMode = ToolMode.Select;

    [ObservableProperty]
    private PhotoTransform _imageTransform = PhotoTransform.Default;

    [ObservableProperty]
    private string? _referenceImageUri;

    public ConstructorViewModel(PathologyRepository repository)
    {
        _repository = repository;
    }

    public void SelectPathology(string id)
    {
        TargetFile = _repository.ReadPathology(id);
        _dirty.Clear();
        _undoStacks.Clear();
        _redoStacks.Clear();
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

    public void SetSampleRange(Lead lead, int startIndex, IReadOnlyList<int> values)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (startIndex < 0 || startIndex >= stream.Samples.Length) return;

        var newSamples = (int[])stream.Samples.Clone();
        SnapshotForUndo(lead, stream.Samples);

        for (var i = 0; i < values.Count; i++)
        {
            var idx = startIndex + i;
            if (idx < newSamples.Length)
            {
                newSamples[idx] = values[i];
            }
        }

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    private void SnapshotForUndo(Lead lead, int[] oldSamples)
    {
        if (!_undoStacks.TryGetValue(lead, out var stack))
        {
            stack = new Stack<int[]>();
            _undoStacks[lead] = stack;
        }
        stack.Push((int[])oldSamples.Clone());
        _redoStacks.Remove(lead);
    }

    public bool CanUndo(Lead lead) => _undoStacks.TryGetValue(lead, out var s) && s.Count > 0;

    public bool CanRedo(Lead lead) => _redoStacks.TryGetValue(lead, out var s) && s.Count > 0;

    public void Undo(Lead lead)
    {
        if (!CanUndo(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;

        var oldState = _undoStacks[lead].Pop();

        if (!_redoStacks.TryGetValue(lead, out var redo))
        {
            redo = new Stack<int[]>();
            _redoStacks[lead] = redo;
        }
        redo.Push((int[])stream.Samples.Clone());

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(oldState) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    public void Redo(Lead lead)
    {
        if (!CanRedo(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;

        var newState = _redoStacks[lead].Pop();

        if (!_undoStacks.TryGetValue(lead, out var undo))
        {
            undo = new Stack<int[]>();
            _undoStacks[lead] = undo;
        }
        undo.Push((int[])stream.Samples.Clone());

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newState) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    public void CalculateDerivedLeads()
    {
        var file = TargetFile;
        if (file is null) return;

        int baseline = _repository.Manifest()?.Baseline ?? 1024;
        var leads = file.Leads;

        var hasI = leads.TryGetValue(Lead.I, out var streamI);
        var hasII = leads.TryGetValue(Lead.II, out var streamII);
        var hasV2 = leads.TryGetValue(Lead.V2, out var streamV2);
        var hasV6 = leads.TryGetValue(Lead.V6, out var streamV6);

        var newLeads = new Dictionary<Lead, LeadStream>(leads);
        bool anyChanged = false;

        if (hasI && hasII)
        {
            foreach (var target in DerivedLeads.DerivableFromIandII)
            {
                var derived = DerivedLeads.CombineIII_aVR_aVL_aVF(streamI!.Samples, streamII!.Samples, target, baseline);
                if (derived.Length > 0)
                {
                    SnapshotForUndo(target, leads.TryGetValue(target, out var existing) ? existing.Samples : Array.Empty<int>());
                    newLeads[target] = new LeadStream(target, derived);
                    _dirty.Add(target);
                    anyChanged = true;
                }
            }
        }

        if (hasV2 && hasV6)
        {
            foreach (var target in DerivedLeads.DerivableFromV2andV6)
            {
                var derived = DerivedLeads.CombineV1_V3_V4_V5(streamV2!.Samples, streamV6!.Samples, target, baseline);
                if (derived.Length > 0)
                {
                    SnapshotForUndo(target, leads.TryGetValue(target, out var existing) ? existing.Samples : Array.Empty<int>());
                    newLeads[target] = new LeadStream(target, derived);
                    _dirty.Add(target);
                    anyChanged = true;
                }
            }
        }

        if (anyChanged)
        {
            TargetFile = file with { Leads = newLeads };
            DirtyLeads = _dirty.ToArray();
        }
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

        SnapshotForUndo(lead, file.Leads[lead].Samples);

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
