using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read-only <see cref="IOskeSource"/> backed by an encrypted content pack
/// (<see cref="EncryptedArchive"/>), mirroring <see cref="EncryptedCourseSource"/>. This is the
/// distribution read path for OSCE content: the vendor's forms and answer keys ship as
/// <c>Assets/Oske.pak</c> and are never extracted, so no answer key ever lands in
/// <c>%LOCALAPPDATA%</c> where a student could read the marking scheme off disk.
///
/// <para>The pack preserves the on-disk tree that <see cref="FileOskeSource"/> uses, so the same
/// dataset folder packs verbatim:
/// <code>
/// forms/&lt;formId&gt;.json
/// answers/&lt;ecgId&gt;/&lt;formId&gt;.json
/// </code></para>
///
/// <para>Being read-only it carries no write/delete methods; authored content lives on the writable
/// <see cref="FileOskeSource"/> and the two are joined by <see cref="CompositeOskeSource"/>.</para>
/// </summary>
public sealed class EncryptedOskeSource : IOskeSource, IDisposable
{
    private const string FormsPrefix = "forms/";
    private const string AnswersPrefix = "answers/";
    private const string JsonExt = ".json";

    private readonly EncryptedArchive _archive;
    private IReadOnlyList<OskeForm>? _forms;

    public EncryptedOskeSource(EncryptedArchive archive)
    {
        _archive = archive;
    }

    /// <summary>
    /// Opens the pack at <paramref name="packPath"/> and wraps it. Throws on a bad pack.
    ///
    /// <para>Read fully into memory so the file handle is released immediately, like
    /// <see cref="EncryptedCourseSource.Open"/>: a held handle denies writers, so re-exporting a pack
    /// over a path already in use would fail. An OSCE pack is tens of kilobytes.</para>
    /// </summary>
    public static EncryptedOskeSource Open(string packPath) =>
        new(EncryptedArchive.OpenBytes(File.ReadAllBytes(packPath)));

    /// <summary>Parsed once and cached — the pack cannot change under a running process.</summary>
    public IReadOnlyList<OskeForm> ReadForms() => _forms ??= ParseForms();

    public OskeForm? ReadForm(string formId) =>
        string.IsNullOrEmpty(formId)
            ? null
            : ReadForms().FirstOrDefault(f => string.Equals(f.FormId, formId, StringComparison.Ordinal));

    public OskeAnswerKey? ReadAnswerKey(string ecgId, string formId)
    {
        if (string.IsNullOrEmpty(ecgId) || string.IsNullOrEmpty(formId)) return null;
        try
        {
            var text = _archive.ReadPathText($"{AnswersPrefix}{ecgId}/{formId}{JsonExt}");
            return text is null ? null : OskeJson.DeserializeAnswerKey(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ecg ids with a packed answer key for <paramref name="formId"/>, ordered like
    /// <see cref="FileOskeSource.ListAnswerKeyEcgIds"/> so the start dialog's list does not reshuffle
    /// depending on which half a key came from.</summary>
    public IReadOnlyList<string> ListAnswerKeyEcgIds(string formId)
    {
        if (string.IsNullOrEmpty(formId)) return Array.Empty<string>();
        var suffix = $"/{formId}{JsonExt}";
        try
        {
            return _archive.EntryPaths
                .Where(p => p.StartsWith(AnswersPrefix, StringComparison.OrdinalIgnoreCase) &&
                            p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[AnswersPrefix.Length..^suffix.Length])   // "<ecgId>"
                .Where(id => id.Length > 0 && !id.Contains('/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Matches <see cref="FileOskeSource.IsValid"/>: at least one form template is present. Checks for
    /// the entry rather than deserializing it — this runs on the UI thread while the app view-model is
    /// being built, and the check should not grow with the dataset.
    /// </summary>
    public bool IsValid() =>
        _archive.EntryPaths.Any(p =>
            p.StartsWith(FormsPrefix, StringComparison.OrdinalIgnoreCase) &&
            p.EndsWith(JsonExt, StringComparison.OrdinalIgnoreCase) &&
            p.IndexOf('/', FormsPrefix.Length) < 0);

    private IReadOnlyList<OskeForm> ParseForms()
    {
        try
        {
            var forms = new List<OskeForm>();
            foreach (var path in _archive.EntryPaths)
            {
                if (!path.StartsWith(FormsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.EndsWith(JsonExt, StringComparison.OrdinalIgnoreCase)) continue;
                // Only the immediate forms/ children, never a nested tree.
                if (path.IndexOf('/', FormsPrefix.Length) >= 0) continue;
                try
                {
                    if (_archive.ReadPathText(path) is { } text &&
                        OskeJson.DeserializeForm(text) is { } form)
                        forms.Add(form);
                }
                catch
                {
                    // skip an unreadable form entry, exactly as the file source does
                }
            }
            return forms;
        }
        catch
        {
            // A damaged or foreign pack reads as no forms rather than taking the app down; the
            // instructor's own content (if any) still comes through the composite source.
            return Array.Empty<OskeForm>();
        }
    }

    public void Dispose() => _archive.Dispose();
}
