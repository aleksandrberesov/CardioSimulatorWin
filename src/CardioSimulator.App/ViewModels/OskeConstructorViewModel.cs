using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Authors the reference answers («эталон») for one ECG under one specialty's form, then saves them
/// via <see cref="OskeRepository.WriteAnswerKey"/> — the piece that lets a teacher build the answer
/// keys the exam grades against. Modeled on the course constructor's edit-then-save flow.
/// </summary>
public sealed class OskeConstructorViewModel
{
    private readonly OskeRepository _repository;
    private readonly Dictionary<string, HashSet<string>> _correct = new();

    public OskeConstructorViewModel(OskeRepository repository)
    {
        _repository = repository;
    }

    public OskeRepository Repository => _repository;

    public OskeSpecialty Specialty { get; private set; } = OskeSpecialty.Therapy;
    public string? EcgId { get; private set; }
    public OskeForm? Form { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>Loads the form for the specialty and any existing answer key for the ECG.</summary>
    public void Select(OskeSpecialty specialty, string ecgId)
    {
        Specialty = specialty;
        EcgId = ecgId;
        Form = _repository.FormFor(specialty);
        _correct.Clear();
        if (_repository.AnswerKey(ecgId, specialty) is { } key)
        {
            foreach (var (questionId, options) in key.CorrectOptionIds)
                _correct[questionId] = new HashSet<string>(options);
        }
        IsDirty = false;
    }

    public bool HasExistingKey =>
        EcgId is not null && _repository.AnswerKey(EcgId, Specialty) is not null;

    public bool IsCorrect(string questionId, string optionId) =>
        _correct.TryGetValue(questionId, out var s) && s.Contains(optionId);

    public void SetSingle(string questionId, string optionId)
    {
        _correct[questionId] = new HashSet<string> { optionId };
        IsDirty = true;
    }

    public void ToggleMulti(string questionId, string optionId, bool on)
    {
        if (!_correct.TryGetValue(questionId, out var set))
        {
            set = new HashSet<string>();
            _correct[questionId] = set;
        }
        if (on) set.Add(optionId);
        else set.Remove(optionId);
        if (set.Count == 0) _correct.Remove(questionId);
        IsDirty = true;
    }

    /// <summary>Persists the authored answers as the ECG's key. Returns false if nothing is loaded.</summary>
    public bool Save()
    {
        if (Form is null || EcgId is null) return false;
        var map = Form.Questions.ToDictionary(
            q => q.Id,
            q => (IReadOnlyList<string>)(_correct.TryGetValue(q.Id, out var s) ? s.ToList() : new List<string>()));
        var ok = _repository.WriteAnswerKey(new OskeAnswerKey(EcgId, Form.FormId, map));
        if (ok) IsDirty = false;
        return ok;
    }
}
