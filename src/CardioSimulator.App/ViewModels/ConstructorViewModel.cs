using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Edits a single <see cref="PathologyFile"/> at the raw-ADC level. Faithful port of the
/// Android <c>ConstructorViewModel</c>:
/// <list type="bullet">
/// <item>Pick a pathology, focus a lead, select a sample, nudge/set its ADC value.</item>
/// <item>Edits run through a weighted kernel (Cosine/Spline/Bezier/LOESS/MLS) over
/// <see cref="EditingRadius"/>; per-lead float accumulators in <see cref="_floatBuffers"/>
/// turn a sequence of +/-1 nudges into a smooth bump (no "block-of-samples" artifact).</item>
/// <item>Per-stroke undo/redo via <see cref="StartStroke"/> + <see cref="TraceSamples"/>,
/// capped at <see cref="MaxUndoDepth"/> snapshots per lead.</item>
/// <item>Derived leads (III/aVR/aVL/aVF, V1/V3/V4/V5) are read-only (see
/// <see cref="IsLeadEditable"/>); writes silently no-op for them.</item>
/// </list>
/// </summary>
public partial class ConstructorViewModel : ObservableObject
{
    public const int AdcMin = 0;
    public const int AdcMax = 2048;
    public const int DefaultEditingRadius = 100;
    public const int MinEditingRadius = 1;
    public const int MaxEditingRadius = 1000;
    public const int MaxUndoDepth = 20;

    private readonly PathologyRepository _repository;
    private readonly HashSet<Lead> _dirty = new();

    /// <summary>
    /// Per-lead sub-integer accumulator parallel to <c>LeadStream.Samples</c>. Sub-integer
    /// weighted contributions land here so a sequence of +/-1 nudges builds a smooth bump
    /// even where each per-call weight rounds to 0. Invariant: when present,
    /// <c>floatBuffers[lead][i].roundToInt() == TargetFile.Leads[lead].Samples[i]</c>.
    /// Mutators that change samples outside of <see cref="AdjustSample"/> must keep this
    /// in sync (see <see cref="SetSample"/>, <see cref="RevertLead"/>,
    /// <see cref="CalculateDerivedLeads"/>, <see cref="SelectPathology"/>).
    /// </summary>
    private readonly Dictionary<Lead, float[]> _floatBuffers = new();

    /// <summary>Per-lead per-stroke undo snapshot ring (oldest first; trimmed to <see cref="MaxUndoDepth"/>).</summary>
    private readonly Dictionary<Lead, LinkedList<int[]>> _undoStacks = new();
    private readonly Dictionary<Lead, LinkedList<int[]>> _redoStacks = new();

    [ObservableProperty] private PathologyFile? _targetFile;
    [ObservableProperty] private Lead _focusedLead = Lead.II;
    [ObservableProperty] private IReadOnlyCollection<Lead> _dirtyLeads = Array.Empty<Lead>();
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isMetadataDirty;
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private ToolMode _toolMode = ToolMode.Select;
    [ObservableProperty] private PhotoTransform _imageTransform = PhotoTransform.Default;
    [ObservableProperty] private string? _referenceImageUri;

    /// <summary>Active smoothing kernel for sample edits.</summary>
    [ObservableProperty] private EditingAlgorithm _algorithm = EditingAlgorithm.Cosine;

    /// <summary>Half-width of the smoothing kernel, in samples. Clamped to [<see cref="MinEditingRadius"/>, <see cref="MaxEditingRadius"/>].</summary>
    [ObservableProperty] private int _editingRadius = DefaultEditingRadius;

    /// <summary>Auto-detected candidate trace (ADC array), null when none pending.</summary>
    [ObservableProperty] private int[]? _ghostTrace;

    public ConstructorViewModel(PathologyRepository repository)
    {
        _repository = repository;
    }

    // ── Selection ───────────────────────────────────────────────────────────

    public void SelectPathology(string id)
    {
        var file = _repository.ReadPathology(id);
        // The group lives in the manifest entry; seed it onto the in-memory file when the .dat
        // header doesn't carry it yet (legacy data), so the editor shows the current group and a
        // save doesn't wipe it.
        if (file is { Group: null })
        {
            var group = _repository.Manifest()?.Entries.FirstOrDefault(e => e.Id == id)?.Group;
            if (group is not null) file = file with { Group = group };
        }
        TargetFile = file;
        _dirty.Clear();
        _floatBuffers.Clear();
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

    // ── Lead editability ────────────────────────────────────────────────────

    /// <summary>Derived leads (III/aVR/aVL/aVF/V1/V3/V4/V5) are read-only.</summary>
    public static bool IsLeadEditable(Lead lead) =>
        !DerivedLeads.DerivableFromIandII.Contains(lead) &&
        !DerivedLeads.DerivableFromV2andV6.Contains(lead);

    // ── Settings ────────────────────────────────────────────────────────────

    public void SetEditingAlgorithm(EditingAlgorithm algorithm) => Algorithm = algorithm;

    public void SetEditingRadius(int radius) =>
        EditingRadius = Math.Clamp(radius, MinEditingRadius, MaxEditingRadius);

    // ── Editing ─────────────────────────────────────────────────────────────

    public void MoveSelectedUp() => AdjustSample(FocusedLead, SelectedIndex, +1);
    public void MoveSelectedDown() => AdjustSample(FocusedLead, SelectedIndex, -1);

    /// <summary>
    /// Apply a +/-delta nudge centered on <paramref name="index"/>, weighted across
    /// <see cref="EditingRadius"/> by <see cref="Algorithm"/>. Sub-integer contributions
    /// accumulate in <see cref="_floatBuffers"/> so a sequence of nudges builds a genuinely
    /// smooth bump even where each per-call weight rounds to 0. Refuses non-editable leads.
    /// </summary>
    private void AdjustSample(Lead lead, int index, int delta)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (index < 0 || index >= stream.Samples.Length) return;

        var radius = EditingRadius;
        var algorithm = Algorithm;
        var deltaF = (float)delta;
        var floatBuf = FloatBufferFor(lead, stream.Samples);
        var newSamples = (int[])stream.Samples.Clone();

        for (var d = -radius; d <= radius; d++)
        {
            var targetIndex = index + d;
            if (targetIndex < 0 || targetIndex >= newSamples.Length) continue;
            var weight = ComputeWeight(d, radius, algorithm);
            if (weight == 0f) continue;
            floatBuf[targetIndex] = Math.Clamp(floatBuf[targetIndex] + deltaF * weight, (float)AdcMin, (float)AdcMax);
            newSamples[targetIndex] = (int)MathF.Round(floatBuf[targetIndex]);
        }

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    /// <summary>
    /// Sets an absolute ADC value at <paramref name="index"/>, applying the same weighted
    /// kernel as <see cref="AdjustSample"/> so an absolute-value jump from a dialog produces
    /// a smooth bump rather than a single-sample spike.
    /// </summary>
    public void SetSample(Lead lead, int index, int adcValue)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (index < 0 || index >= stream.Samples.Length) return;
        var target = Math.Clamp(adcValue, AdcMin, AdcMax);
        if (stream.Samples[index] == target) return;

        var radius = EditingRadius;
        var algorithm = Algorithm;
        var deltaF = (float)(target - stream.Samples[index]);
        var floatBuf = FloatBufferFor(lead, stream.Samples);
        var newSamples = (int[])stream.Samples.Clone();

        for (var d = -radius; d <= radius; d++)
        {
            var targetIndex = index + d;
            if (targetIndex < 0 || targetIndex >= newSamples.Length) continue;
            var weight = ComputeWeight(d, radius, algorithm);
            if (weight == 0f) continue;
            floatBuf[targetIndex] = Math.Clamp(floatBuf[targetIndex] + deltaF * weight, (float)AdcMin, (float)AdcMax);
            newSamples[targetIndex] = (int)MathF.Round(floatBuf[targetIndex]);
        }

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    /// <summary>
    /// Direct-write range setter (no kernel). Snapshots for undo first, so callers don't
    /// need to wrap it in <see cref="StartStroke"/>. Useful for restore/undo paths.
    /// </summary>
    public void SetSampleRange(Lead lead, int startIndex, IReadOnlyList<int> values)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (startIndex < 0 || startIndex >= stream.Samples.Length) return;

        PushUndo(lead, stream.Samples);
        var newSamples = (int[])stream.Samples.Clone();
        var floatBuf = FloatBufferFor(lead, stream.Samples);

        for (var i = 0; i < values.Count; i++)
        {
            var idx = startIndex + i;
            if (idx >= newSamples.Length) break;
            var clamped = Math.Clamp(values[i], AdcMin, AdcMax);
            newSamples[idx] = clamped;
            floatBuf[idx] = clamped;
        }

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    // ── Element library (generate + insert) ────────────────────────────────

    /// <summary>
    /// Generates an ECG <paramref name="element"/> (P / QRS / T / ST / baseline) and writes it into
    /// the focused lead starting at <see cref="SelectedIndex"/>, undoable via
    /// <see cref="SetSampleRange"/>. The author then fine-tunes it with the point editor. Refuses
    /// derived/read-only leads. Returns false if nothing was written.
    /// </summary>
    public bool InsertElement(EcgElement element, EcgElementParams parameters, EcgCalibration calibration)
    {
        var lead = FocusedLead;
        if (!IsLeadEditable(lead)) return false;
        var file = TargetFile;
        if (file is null || !file.Leads.ContainsKey(lead)) return false;

        var baseline = _repository.Manifest()?.Baseline ?? 1024;
        var start = SelectedIndex;
        var segment = EcgElementGenerator.Generate(element, parameters, calibration, baseline);
        SetSampleRange(lead, start, segment);
        RecordElement(lead, new EcgElementInstance(element, start, segment.Length, parameters.AmplitudeMv));
        SelectIndex(start + segment.Length);
        return true;
    }

    /// <summary>Placed elements annotating <paramref name="lead"/> (ordered by start index).</summary>
    public IReadOnlyList<EcgElementInstance> ElementsFor(Lead lead) =>
        TargetFile is { } f && f.Leads.TryGetValue(lead, out var s) ? s.Elements : Array.Empty<EcgElementInstance>();

    /// <summary>
    /// Re-applies width/height to a previously placed element: regenerates its shape over the lead's
    /// samples starting at the element's recorded position (undoable). Widening overwrites following
    /// samples; narrowing baseline-fills the freed tail. Updates the recorded length/amplitude.
    /// </summary>
    public void ResizeElement(Lead lead, int elementIndex, float durationMs, float amplitudeMv, EcgCalibration calibration)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (elementIndex < 0 || elementIndex >= stream.Elements.Count) return;
        var inst = stream.Elements[elementIndex];

        var baseline = _repository.Manifest()?.Baseline ?? 1024;
        var segment = EcgElementGenerator.Generate(inst.Type, new EcgElementParams(durationMs, amplitudeMv), calibration, baseline);
        var span = Math.Max(segment.Length, inst.Length);
        var values = new int[span];
        for (var i = 0; i < span; i++) values[i] = i < segment.Length ? segment[i] : baseline;

        SetSampleRange(lead, inst.StartIndex, values); // undoable; replaces TargetFile (elements preserved)

        var updated = stream.Elements.ToList();
        updated[elementIndex] = inst with { Length = segment.Length, AmplitudeMv = amplitudeMv };
        ApplyElements(lead, updated);
    }

    /// <summary>Deletes a placed element: erases its span back to baseline (undoable) and drops the
    /// annotation.</summary>
    public void RemoveElement(Lead lead, int elementIndex)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        if (elementIndex < 0 || elementIndex >= stream.Elements.Count) return;
        var inst = stream.Elements[elementIndex];

        var baseline = _repository.Manifest()?.Baseline ?? 1024;
        var flat = new int[inst.Length];
        Array.Fill(flat, baseline);
        SetSampleRange(lead, inst.StartIndex, flat); // undoable; replaces TargetFile

        var updated = stream.Elements.ToList();
        updated.RemoveAt(elementIndex);
        ApplyElements(lead, updated);
    }

    private void RecordElement(Lead lead, EcgElementInstance instance)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        var list = stream.Elements.ToList();
        list.Add(instance);
        list.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
        ApplyElements(lead, list);
    }

    /// <summary>Writes a new element annotation list for <paramref name="lead"/> onto the current
    /// <see cref="TargetFile"/> (reads it fresh, since sample writes may have just replaced it).</summary>
    private void ApplyElements(Lead lead, IReadOnlyList<EcgElementInstance> elements)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithElements(elements) };
        TargetFile = file with { Leads = newLeads };
        IsMetadataDirty = true;
    }

    // ── Stroke API (for freehand trace / batch writes) ─────────────────────

    /// <summary>Snapshots the current samples of <paramref name="lead"/> for undo. Call at <c>PointerPressed</c>.</summary>
    public void StartStroke(Lead lead)
    {
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;
        PushUndo(lead, stream.Samples);
        _redoStacks.Remove(lead);
    }

    /// <summary>Batch-writes (index → ADC) pairs into the lead, bypassing the kernel. Call from drag handlers.</summary>
    public void TraceSamples(Lead lead, IReadOnlyDictionary<int, int> updates)
    {
        if (!IsLeadEditable(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;

        var newSamples = (int[])stream.Samples.Clone();
        var floatBuf = FloatBufferFor(lead, stream.Samples);

        foreach (var (index, value) in updates)
        {
            if (index < 0 || index >= newSamples.Length) continue;
            var clamped = Math.Clamp(value, AdcMin, AdcMax);
            newSamples[index] = clamped;
            floatBuf[index] = clamped;
        }

        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(newSamples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
    }

    // ── Undo / Redo (per-stroke ring, capped at MaxUndoDepth) ──────────────

    public bool CanUndo(Lead lead) => _undoStacks.TryGetValue(lead, out var l) && l.Count > 0;
    public bool CanRedo(Lead lead) => _redoStacks.TryGetValue(lead, out var l) && l.Count > 0;

    public void Undo(Lead lead)
    {
        if (!CanUndo(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;

        PushRedo(lead, stream.Samples);
        var oldState = PopUndo(lead)!;
        ApplyRestoredSamples(file, lead, stream, oldState);
    }

    public void Redo(Lead lead)
    {
        if (!CanRedo(lead)) return;
        var file = TargetFile;
        if (file is null || !file.Leads.TryGetValue(lead, out var stream)) return;

        PushUndoNoTrim(lead, stream.Samples);
        var nextState = PopRedo(lead)!;
        ApplyRestoredSamples(file, lead, stream, nextState);
    }

    private void ApplyRestoredSamples(PathologyFile file, Lead lead, LeadStream stream, int[] samples)
    {
        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = stream.WithSamples(samples) };
        TargetFile = file with { Leads = newLeads };
        _dirty.Add(lead);
        DirtyLeads = _dirty.ToArray();
        _floatBuffers.Remove(lead); // force re-sync from the restored ints
    }

    private void PushUndo(Lead lead, int[] state)
    {
        if (!_undoStacks.TryGetValue(lead, out var list))
        {
            list = new LinkedList<int[]>();
            _undoStacks[lead] = list;
        }
        list.AddLast((int[])state.Clone());
        while (list.Count > MaxUndoDepth) list.RemoveFirst();
    }

    /// <summary>Push to undo without trimming — used by <see cref="Redo"/> so it can mirror Android's stack push.</summary>
    private void PushUndoNoTrim(Lead lead, int[] state)
    {
        if (!_undoStacks.TryGetValue(lead, out var list))
        {
            list = new LinkedList<int[]>();
            _undoStacks[lead] = list;
        }
        list.AddLast((int[])state.Clone());
    }

    private void PushRedo(Lead lead, int[] state)
    {
        if (!_redoStacks.TryGetValue(lead, out var list))
        {
            list = new LinkedList<int[]>();
            _redoStacks[lead] = list;
        }
        list.AddLast((int[])state.Clone());
    }

    private int[]? PopUndo(Lead lead)
    {
        if (!_undoStacks.TryGetValue(lead, out var list) || list.Count == 0) return null;
        var top = list.Last!.Value;
        list.RemoveLast();
        return top;
    }

    private int[]? PopRedo(Lead lead)
    {
        if (!_redoStacks.TryGetValue(lead, out var list) || list.Count == 0) return null;
        var top = list.Last!.Value;
        list.RemoveLast();
        return top;
    }

    // ── Derived leads / annotation / revert / rename / save ────────────────

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
        var anyChanged = false;

        if (hasI && hasII)
        {
            foreach (var target in DerivedLeads.DerivableFromIandII)
            {
                var derived = DerivedLeads.CombineIII_aVR_aVL_aVF(streamI!.Samples, streamII!.Samples, target, baseline);
                if (derived.Length > 0)
                {
                    PushUndo(target, leads.TryGetValue(target, out var existing) ? existing.Samples : Array.Empty<int>());
                    newLeads[target] = new LeadStream(target, derived);
                    _floatBuffers.Remove(target);
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
                    PushUndo(target, leads.TryGetValue(target, out var existing) ? existing.Samples : Array.Empty<int>());
                    newLeads[target] = new LeadStream(target, derived);
                    _floatBuffers.Remove(target);
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

    public void SetSignificantPoints(IReadOnlyList<SignificantPoint> points)
    {
        var file = TargetFile;
        if (file is null) return;
        TargetFile = file with { SignificantPoints = points.ToList() };
        IsMetadataDirty = true;
    }

    public void RevertLead(Lead lead)
    {
        var file = TargetFile;
        if (file is null) return;
        var original = _repository.ReadPathology(file.Id);
        if (original is null || !original.Leads.TryGetValue(lead, out var originalStream)) return;

        PushUndo(lead, file.Leads[lead].Samples);
        var newLeads = new Dictionary<Lead, LeadStream>(file.Leads) { [lead] = originalStream };
        TargetFile = file with { Leads = newLeads };
        _dirty.Remove(lead);
        DirtyLeads = _dirty.ToArray();
        _floatBuffers.Remove(lead);
    }

    /// <summary>Current pathology's group key (null = ungrouped).</summary>
    public string? CurrentGroup => TargetFile?.Group;

    /// <summary>Sets the current pathology's group; persisted to the .dat header + manifest on save.</summary>
    public void SetGroup(string? group)
    {
        var file = TargetFile;
        if (file is null) return;
        var normalized = string.IsNullOrWhiteSpace(group) ? null : group;
        if (file.Group == normalized) return;
        TargetFile = file with { Group = normalized };
        IsMetadataDirty = true;
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

    // ── Reference image (photo tracing Phase A) ────────────────────────────

    public void SetImageOffset(float x, float y)
    {
        if (ImageTransform.IsLocked) return;
        ImageTransform = ImageTransform with { OffsetX = x, OffsetY = y };
    }

    public void SetImageScale(float scale)
    {
        if (ImageTransform.IsLocked) return;
        ImageTransform = ImageTransform with { Scale = scale };
    }

    public void SetImageRotation(float deg)
    {
        if (ImageTransform.IsLocked) return;
        ImageTransform = ImageTransform with { RotationDeg = deg };
    }

    public void SetImageAlpha(float alpha) =>
        ImageTransform = ImageTransform with { Alpha = Math.Clamp(alpha, 0f, 1f) };

    public void SetImageVisible(bool visible) =>
        ImageTransform = ImageTransform with { IsVisible = visible };

    public void SetImageLocked(bool locked) =>
        ImageTransform = ImageTransform with { IsLocked = locked };

    public void ResetImageTransform() =>
        ImageTransform = ImageTransform with { OffsetX = 0f, OffsetY = 0f, Scale = 1f, RotationDeg = 0f };

    /// <summary>Sets the reference-image URI; auto-switches tool mode and resets the transform.</summary>
    public void SetReferenceImageUri(string? uri)
    {
        ReferenceImageUri = uri;
        ToolMode = uri is null ? ToolMode.Select : ToolMode.Photo;
        if (uri is not null) ResetImageTransform();
        if (uri is null) GhostTrace = null;
    }

    /// <summary>Sets (or clears) a candidate trace from auto-detect; render shows it overlaid.</summary>
    public void SetGhostTrace(int[]? trace) => GhostTrace = trace;

    /// <summary>Writes the ghost trace into the focused lead's samples (per-stroke undoable) and clears it.</summary>
    public void ApplyGhostTrace()
    {
        var trace = GhostTrace;
        if (trace is null) return;
        var lead = FocusedLead;
        StartStroke(lead);
        var updates = new Dictionary<int, int>(trace.Length);
        for (var i = 0; i < trace.Length; i++) updates[i] = trace[i];
        TraceSamples(lead, updates);
        GhostTrace = null;
    }

    // ── Pathology lifecycle ────────────────────────────────────────────────

    /// <summary>Creates a blank pathology with flat-baseline leads and selects it. Returns the new id or null.</summary>
    public string? CreateNewPathology(string titleEn, string? nameRu)
    {
        var newId = _repository.CreatePathology(titleEn, nameRu);
        if (newId is not null) SelectPathology(newId);
        return newId;
    }

    /// <summary>
    /// Imports a ready-made pathology (e.g. converted from a WFDB record), persists it under a fresh
    /// id, and selects it for editing. Returns the new id or null on failure.
    /// </summary>
    public string? ImportPathology(PathologyFile file)
    {
        var newId = _repository.ImportPathology(file);
        if (newId is not null) SelectPathology(newId);
        return newId;
    }

    /// <summary>Duplicates the current pathology, assigns new titles, and selects the copy. No-op if no target.</summary>
    public void DuplicateCurrentPathology(string titleEn, string? nameRu)
    {
        var file = TargetFile;
        if (file is null) return;
        var newId = _repository.DuplicatePathology(file.Id);
        if (newId is null) return;
        SelectPathology(newId);
        if (TargetFile is not null)
        {
            TargetFile = TargetFile with { TitleEn = titleEn, NameRu = nameRu };
            IsMetadataDirty = true;
        }
    }

    /// <summary>Deletes the current pathology + manifest entry; clears editor state.</summary>
    public bool DeleteCurrentPathology()
    {
        var file = TargetFile;
        if (file is null) return false;
        if (!_repository.DeletePathology(file.Id)) return false;

        TargetFile = null;
        _dirty.Clear();
        _floatBuffers.Clear();
        _undoStacks.Clear();
        _redoStacks.Clear();
        DirtyLeads = Array.Empty<Lead>();
        IsMetadataDirty = false;
        return true;
    }

    // ── Kernel + buffer helpers ────────────────────────────────────────────

    private float[] FloatBufferFor(Lead lead, int[] samples)
    {
        if (_floatBuffers.TryGetValue(lead, out var existing) && existing.Length == samples.Length) return existing;
        var fresh = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) fresh[i] = samples[i];
        _floatBuffers[lead] = fresh;
        return fresh;
    }

    /// <summary>Weight at relative offset <paramref name="d"/> from the center, for the chosen kernel.</summary>
    private static float ComputeWeight(int d, int radius, EditingAlgorithm algorithm)
    {
        var t = MathF.Abs((float)d / radius);
        if (t > 1f) return 0f;
        return algorithm switch
        {
            EditingAlgorithm.Cosine => 0.5f * (1f + MathF.Cos(MathF.PI * d / radius)),
            // Smoothstep (Hermite h01): zero slope at center and edge.
            EditingAlgorithm.Spline => 1f - (-2f * MathF.Pow(t, 3) + 3f * MathF.Pow(t, 2)),
            // (1 - t^2)^2: zero slope at d=0 (no kink) and zero at the edge.
            EditingAlgorithm.Bezier => MathF.Pow(1f - MathF.Pow(t, 2), 2),
            EditingAlgorithm.LOESS => MathF.Pow(1f - MathF.Pow(t, 3), 3),
            // Truncated, re-normalised Gaussian: smoothly reaches 0 at |d|=radius.
            EditingAlgorithm.MLS => MlsWeight(t),
            _ => 0f,
        };
    }

    private static float MlsWeight(float t)
    {
        const float sigma = 0.4f;
        var raw = MathF.Exp(-MathF.Pow(t, 2) / (2f * MathF.Pow(sigma, 2)));
        var edge = MathF.Exp(-1f / (2f * MathF.Pow(sigma, 2)));
        return MathF.Max(0f, (raw - edge) / (1f - edge));
    }
}
