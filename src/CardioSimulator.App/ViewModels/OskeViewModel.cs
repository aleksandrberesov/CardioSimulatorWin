using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Drives one OSCE exam attempt: holds the active form, the student's per-block selections, the
/// loaded answer key, and the graded result. Grading + persistence delegate to the Core
/// <see cref="OskeGrader"/> and <see cref="OskeResultStore"/>. The screen orchestrates it
/// imperatively (start → collect selections → submit).
/// </summary>
public sealed class OskeViewModel
{
    private readonly OskeRepository _repository;
    private readonly OskeResultStore _resultStore;
    private readonly Dictionary<string, HashSet<string>> _selections = new();
    private OskeAnswerKey? _key;

    public OskeViewModel(OskeRepository repository, OskeResultStore resultStore)
    {
        _repository = repository;
        _resultStore = resultStore;
    }

    public OskeRepository Repository => _repository;

    public OskeForm? Form { get; private set; }
    public OskeStudentInfo? Student { get; private set; }
    public OskeSpecialty Specialty { get; private set; }
    public string? EcgId { get; private set; }
    public OskeResult? Result { get; private set; }

    /// <summary>True once an attempt has started and before it has been submitted.</summary>
    public bool IsTakingExam => Form is not null && Result is null;

    /// <summary>Ecg ids with an authored answer key for <paramref name="specialty"/>.</summary>
    public IReadOnlyList<string> AvailableEcgIds(OskeSpecialty specialty) =>
        _repository.AnswerKeyEcgIds(specialty);

    public void StartAttempt(OskeStudentInfo student, OskeSpecialty specialty, string ecgId)
    {
        Student = student;
        Specialty = specialty;
        EcgId = ecgId;
        Form = _repository.FormFor(specialty);
        _key = _repository.AnswerKey(ecgId, specialty);
        _selections.Clear();
        Result = null;
    }

    public bool IsSelected(string questionId, string optionId) =>
        _selections.TryGetValue(questionId, out var s) && s.Contains(optionId);

    /// <summary>Picks a single answer for a block (replaces any previous pick).</summary>
    public void SetSingle(string questionId, string optionId) =>
        _selections[questionId] = new HashSet<string> { optionId };

    /// <summary>Adds/removes one answer for a multi-select block.</summary>
    public void ToggleMulti(string questionId, string optionId, bool on)
    {
        if (!_selections.TryGetValue(questionId, out var set))
        {
            set = new HashSet<string>();
            _selections[questionId] = set;
        }
        if (on) set.Add(optionId);
        else set.Remove(optionId);
        if (set.Count == 0) _selections.Remove(questionId);
    }

    /// <summary>Grades and saves the attempt. Returns null when there is no answer key for this ECG.</summary>
    public OskeResult? Submit()
    {
        if (Form is null || Student is null || EcgId is null || _key is null) return null;

        var selections = Form.Questions.ToDictionary(
            q => q.Id,
            q => (IReadOnlyList<string>)(_selections.TryGetValue(q.Id, out var s) ? s.ToList() : new List<string>()));

        var result = OskeGrader.Grade(Form, _key, selections, Student, EcgId);
        _resultStore.Save(result);
        Result = result;
        return result;
    }

    /// <summary>Clears the attempt back to the pre-start state.</summary>
    public void Reset()
    {
        Form = null;
        Student = null;
        EcgId = null;
        Result = null;
        _key = null;
        _selections.Clear();
    }
}
