using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Caches the question bank and brokers reads/writes. Raises <see cref="Changed"/> when the source
/// swaps or a question is written/deleted/imported (the Test Constructor's bank view listens),
/// mirroring <see cref="TestRepository"/>.
/// </summary>
public class QuestionBankRepository
{
    private IQuestionBankSource _source;
    private IReadOnlyList<TestQuestion>? _questions;

    public QuestionBankRepository(IQuestionBankSource source)
    {
        _source = source;
    }

    public event EventHandler? Changed;

    public void SetSource(IQuestionBankSource newSource)
    {
        _source = newSource;
        _questions = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The bank questions (cached). Empty when none are on disk.</summary>
    public IReadOnlyList<TestQuestion> Questions => _questions ??= _source.ReadQuestions();

    public TestQuestion? Question(string id) => Questions.FirstOrDefault(q => q.Id == id);

    /// <summary>The distinct themes actually used by bank questions (for filter chips).</summary>
    public IReadOnlyList<string> UsedThemes() => Questions
        .Select(q => q.Theme)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Select(t => t!)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public bool WriteQuestion(TestQuestion question)
    {
        if (_source is not FileQuestionBankSource fs) return false;
        var ok = fs.WriteQuestion(question);
        if (ok) Invalidate();
        return ok;
    }

    public bool DeleteQuestion(string id)
    {
        if (_source is not FileQuestionBankSource fs) return false;
        var ok = fs.DeleteQuestion(id);
        if (ok) Invalidate();
        return ok;
    }

    /// <summary>Writes a batch of questions into the bank (overwriting by id). Returns how many were
    /// written successfully — the AI-import entry point.</summary>
    public int Import(IEnumerable<TestQuestion> questions)
    {
        if (_source is not FileQuestionBankSource fs) return 0;
        var written = 0;
        foreach (var q in questions)
            if (fs.WriteQuestion(q)) written++;
        if (written > 0) Invalidate();
        return written;
    }

    /// <summary>Snapshot of all bank questions as a JSON array (the export interchange format).</summary>
    public string ExportAll() => TestJson.SerializeBank(Questions);

    /// <summary>Forces a re-read of the cached questions on next access and notifies listeners.</summary>
    public void Reload() => Invalidate();

    private void Invalidate()
    {
        _questions = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
