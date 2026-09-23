using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read-only <see cref="IQuestionBankSource"/> backed by an encrypted content pack
/// (<see cref="EncryptedArchive"/>), mirroring <see cref="EncryptedCourseSource"/> and
/// <see cref="EncryptedPathologySource"/>. This is the distribution read path for the standing
/// question bank: the vendor pool ships as <c>Assets/QuestionBank.pak</c> and is never extracted, so
/// no question <c>.json</c> lands in <c>%LOCALAPPDATA%</c>.
///
/// <para>The pack holds a single entry, <see cref="BankEntryPath"/> — the bank interchange array that
/// <see cref="TestJson.DeserializeBank"/> reads (the same schema the Test Constructor imports and
/// exports). One file rather than one-per-question is deliberate: the on-disk bank stores a file per
/// question, and re-reading several thousand of those costs tens of seconds on a machine with
/// real-time AV scanning, against well under a second for the single packed array.</para>
///
/// <para>Being read-only it carries no write/delete methods; authored questions live on the writable
/// <see cref="FileQuestionBankSource"/> and the two are joined by
/// <see cref="CompositeQuestionBankSource"/>.</para>
/// </summary>
public sealed class EncryptedQuestionBankSource : IQuestionBankSource, IDisposable
{
    /// <summary>The pack's only entry: the whole bank as one interchange array.</summary>
    public const string BankEntryPath = "bank.json";

    private readonly EncryptedArchive _archive;
    private IReadOnlyList<TestQuestion>? _questions;
    private Dictionary<string, TestQuestion>? _byId;

    public EncryptedQuestionBankSource(EncryptedArchive archive)
    {
        _archive = archive;
    }

    /// <summary>
    /// Opens the pack at <paramref name="packPath"/> and wraps it. Throws on a bad pack.
    ///
    /// <para>Read fully into memory so the file handle is released immediately, like
    /// <see cref="EncryptedCourseSource.Open"/>: a held handle denies writers, which would make
    /// re-exporting a pack over a path already in use fail. A bank pack is a few megabytes.</para>
    /// </summary>
    public static EncryptedQuestionBankSource Open(string packPath) =>
        new(EncryptedArchive.OpenBytes(File.ReadAllBytes(packPath)));

    /// <summary>Parsed once and cached — the pack cannot change under a running process.</summary>
    public IReadOnlyList<TestQuestion> ReadQuestions() => _questions ??= Parse();

    public TestQuestion? ReadQuestion(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        _byId ??= ReadQuestions()
            .GroupBy(q => q.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _byId.TryGetValue(id, out var q) ? q : null;
    }

    /// <summary>A pack is valid when it holds a bank entry that parses to at least one question.</summary>
    public bool IsValid() => ReadQuestions().Count > 0;

    private IReadOnlyList<TestQuestion> Parse()
    {
        try
        {
            var json = _archive.ReadPathText(BankEntryPath);
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TestQuestion>();
            return TestJson.DeserializeBank(json)
                .Where(q => !string.IsNullOrEmpty(q.Id))
                .ToList();
        }
        catch
        {
            // A damaged or foreign pack reads as an empty bank rather than taking the app down;
            // the user's own questions (if any) still come through the composite source.
            return Array.Empty<TestQuestion>();
        }
    }

    public void Dispose() => _archive.Dispose();
}
