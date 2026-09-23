using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Joins the bundled vendor bank (<see cref="EncryptedQuestionBankSource"/>, read-only) with the
/// instructor's own questions on disk (<see cref="FileQuestionBankSource"/>, writable) into the one
/// pool the app reads. The bundled pool ships with the build; anything authored, imported or
/// AI-generated on this machine lands on disk and is layered on top.
///
/// <para><b>Same id wins on disk.</b> A question whose id matches a bundled one replaces it, so an
/// instructor can correct a shipped question by editing it — the edit is written to disk and shadows
/// the packed original. Nothing in the pack is ever modified or removed, exactly as for the bundled
/// course and pathology packs; a shipped question cannot be deleted outright, only shadowed.</para>
///
/// <para>Writes are not part of <see cref="IQuestionBankSource"/>: callers reach the writable half
/// through <see cref="Writable"/>, which <see cref="QuestionBankRepository"/> resolves so that
/// authoring keeps working with a bundled pack present.</para>
/// </summary>
public sealed class CompositeQuestionBankSource : IQuestionBankSource, IDisposable
{
    private readonly IQuestionBankSource _bundled;

    /// <summary>The writable half — where every authored question is stored.</summary>
    public FileQuestionBankSource Writable { get; }

    public CompositeQuestionBankSource(IQuestionBankSource bundled, FileQuestionBankSource writable)
    {
        _bundled = bundled;
        Writable = writable;
    }

    public IReadOnlyList<TestQuestion> ReadQuestions()
    {
        var own = Writable.ReadQuestions();
        var bundled = _bundled.ReadQuestions();
        if (own.Count == 0) return bundled;
        if (bundled.Count == 0) return own;

        var ownIds = new HashSet<string>(own.Select(q => q.Id), StringComparer.Ordinal);
        return bundled
            .Where(q => !ownIds.Contains(q.Id))
            .Concat(own)
            .OrderBy(q => q.Theme ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(q => q.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Disk first, so an edited copy shadows the bundled original.</summary>
    public TestQuestion? ReadQuestion(string id) => Writable.ReadQuestion(id) ?? _bundled.ReadQuestion(id);

    public bool IsValid() => _bundled.IsValid() || Writable.IsValid();

    public void Dispose() => (_bundled as IDisposable)?.Dispose();
}
