using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Joins the bundled OSCE dataset (<see cref="EncryptedOskeSource"/>, read-only) with the
/// instructor's own forms and answer keys on disk (<see cref="FileOskeSource"/>, writable) into the
/// one set of content the app reads. The vendor's stations ship with the build; anything authored in
/// the OSCE constructor lands on disk and is layered on top.
///
/// <para><b>Same id wins on disk.</b> A form with a bundled <c>formId</c>, or an answer key for the
/// same <c>(ecgId, formId)</c>, replaces the packed one — so an instructor can correct a shipped
/// station by editing it. Nothing in the pack is ever modified or removed, exactly as for the bundled
/// course, pathology and question-bank packs.</para>
///
/// <para>Writes are not part of <see cref="IOskeSource"/>: callers reach the writable half through
/// <see cref="Writable"/>, which <see cref="OskeRepository"/> resolves so the constructor keeps
/// authoring with a bundled pack present.</para>
/// </summary>
public sealed class CompositeOskeSource : IOskeSource, IDisposable
{
    private readonly IOskeSource _bundled;

    /// <summary>The writable half — where every authored form and answer key is stored.</summary>
    public FileOskeSource Writable { get; }

    public CompositeOskeSource(IOskeSource bundled, FileOskeSource writable)
    {
        _bundled = bundled;
        Writable = writable;
    }

    public IReadOnlyList<OskeForm> ReadForms()
    {
        var own = Writable.ReadForms();
        var bundled = _bundled.ReadForms();
        if (own.Count == 0) return bundled;
        if (bundled.Count == 0) return own;

        var ownIds = new HashSet<string>(own.Select(f => f.FormId), StringComparer.Ordinal);
        return bundled.Where(f => !ownIds.Contains(f.FormId)).Concat(own).ToList();
    }

    /// <summary>Disk first, so an edited copy shadows the bundled original.</summary>
    public OskeForm? ReadForm(string formId) => Writable.ReadForm(formId) ?? _bundled.ReadForm(formId);

    /// <summary>Disk first, for the same reason — a re-authored answer key overrides the shipped one.</summary>
    public OskeAnswerKey? ReadAnswerKey(string ecgId, string formId) =>
        Writable.ReadAnswerKey(ecgId, formId) ?? _bundled.ReadAnswerKey(ecgId, formId);

    /// <summary>The union of both halves, deduplicated and ordered exactly as either half orders its
    /// own ids — the OSCE start dialog counts and lists these ("N ECGs available"), so a duplicate
    /// would inflate the count.</summary>
    public IReadOnlyList<string> ListAnswerKeyEcgIds(string formId) =>
        _bundled.ListAnswerKeyEcgIds(formId)
            .Concat(Writable.ListAnswerKeyEcgIds(formId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsValid() => _bundled.IsValid() || Writable.IsValid();

    public void Dispose() => (_bundled as IDisposable)?.Dispose();
}
