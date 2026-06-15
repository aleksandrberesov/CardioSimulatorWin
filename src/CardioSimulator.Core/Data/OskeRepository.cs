using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Caches the OSCE form templates and brokers answer-key reads/writes. Raises <see cref="Changed"/>
/// when the source swaps or an answer key / form is written (the constructor + exam screens listen,
/// mirroring <see cref="CourseRepository.ManifestChanged"/>).
/// </summary>
public class OskeRepository
{
    private IOskeSource _source;
    private IReadOnlyList<OskeForm>? _forms;

    public OskeRepository(IOskeSource source)
    {
        _source = source;
    }

    public event EventHandler? Changed;

    public void SetSource(IOskeSource newSource)
    {
        _source = newSource;
        _forms = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The available form templates (cached). Falls back to empty if none on disk.</summary>
    public IReadOnlyList<OskeForm> Forms => _forms ??= _source.ReadForms();

    public OskeForm? Form(string formId) =>
        Forms.FirstOrDefault(f => f.FormId == formId);

    /// <summary>The form for a specialty — on-disk if present, else the built-in seed (never null).</summary>
    public OskeForm FormFor(OskeSpecialty specialty) =>
        Form(OskeForms.FormIdFor(specialty)) ?? OskeSeedForms.For(specialty);

    public OskeAnswerKey? AnswerKey(string ecgId, OskeSpecialty specialty) =>
        _source.ReadAnswerKey(ecgId, OskeForms.FormIdFor(specialty));

    public OskeAnswerKey? AnswerKey(string ecgId, string formId) =>
        _source.ReadAnswerKey(ecgId, formId);

    /// <summary>Ecg ids that have an authored answer key for the specialty's form.</summary>
    public IReadOnlyList<string> AnswerKeyEcgIds(OskeSpecialty specialty) =>
        _source.ListAnswerKeyEcgIds(OskeForms.FormIdFor(specialty));

    public bool HasAnswerKey(string ecgId, OskeSpecialty specialty) =>
        AnswerKey(ecgId, specialty) is not null;

    public bool WriteAnswerKey(OskeAnswerKey key)
    {
        if (_source is not FileOskeSource fs) return false;
        var ok = fs.WriteAnswerKey(key);
        if (ok) Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public bool WriteForm(OskeForm form)
    {
        if (_source is not FileOskeSource fs) return false;
        var ok = fs.WriteForm(form);
        if (ok)
        {
            _forms = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    /// <summary>Forces a re-read of the cached forms on next access and notifies listeners.</summary>
    public void Reload()
    {
        _forms = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
