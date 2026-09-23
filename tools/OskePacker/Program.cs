using System.IO.Compression;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

// Packs an OSCE dataset folder into the encrypted pack the app ships as Assets/Oske.pak. Usage:
//
//   dotnet run --project tools/OskePacker -- <datasetDir> <out.pak>
//
// <datasetDir> is the layout FileOskeSource uses and EncryptedOskeSource reads back verbatim:
//
//   forms/<formId>.json
//   answers/<ecgId>/<formId>.json
//
// Answer keys are the marking scheme, which is the reason this ships encrypted rather than as loose
// files: nothing is written to %LOCALAPPDATA%, so a student cannot read the key off disk.
//
// This tool is the LAST gate before vendor content reaches students, so it validates the dataset
// semantically rather than only checking that the JSON parses. The failure it exists to prevent:
// OskeGrader resolves each block as `key.CorrectOptionIds.TryGetValue(question.Id)` and falls back to
// an EMPTY correct set when the id is missing, and a block counts as correct only when the student's
// selection set equals that. So a key whose ids do not line up with its form marks every block correct
// only for a student who answered nothing - with the customer's PassFraction of 1.0, a student who
// answers everything correctly scores 0/N. Nothing downstream reports that; it just looks like a
// failed exam.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: OskePacker <datasetDir> <out.pak>");
    return 2;
}

var sourceDir = Path.GetFullPath(args[0]);
var outPath = args[1];

if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"dataset folder not found: {sourceDir}");
    return 2;
}

// Collect the tree first, as forward-slash relative paths - the archive addresses entries that way.
var entries = new List<(string Rel, string Full)>();
foreach (var full in Directory.GetFiles(sourceDir, "*.json", SearchOption.AllDirectories))
{
    var rel = Path.GetRelativePath(sourceDir, full).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    if (!rel.StartsWith("forms/", StringComparison.OrdinalIgnoreCase) &&
        !rel.StartsWith("answers/", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  skipping (outside forms/ and answers/): {rel}");
        continue;
    }
    entries.Add((rel, full));
}

if (entries.Count == 0)
{
    Console.Error.WriteLine("nothing to pack - expected forms/*.json and answers/<ecgId>/*.json");
    return 1;
}

var errors = new List<string>();

// ── Pass 1: forms ────────────────────────────────────────────────────────────────────────────────
// Parsed into a map first so the answer keys in pass 2 can be checked against the form they name.
var forms = new Dictionary<string, OskeForm>(StringComparer.Ordinal);
foreach (var (rel, full) in entries.Where(e => e.Rel.StartsWith("forms/", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(e => e.Rel, StringComparer.Ordinal))
{
    if (OskeJson.DeserializeForm(File.ReadAllText(full)) is not { } form)
    {
        errors.Add($"{rel}: does not parse as a form");
        continue;
    }

    // EncryptedOskeSource.ReadForm addresses a form by file name, so a disagreeing FormId would make
    // the form unreachable under the id every caller asks for.
    var expected = Path.GetFileNameWithoutExtension(rel);
    if (!string.Equals(form.FormId, expected, StringComparison.Ordinal))
    {
        errors.Add($"{rel}: formId '{form.FormId}' does not match the file name '{expected}'");
        continue;
    }

    // Several call sites do form.Questions.ToDictionary(q => q.Id) - OSKEScreen's result detail,
    // OskeViewModel.Submit, OskeConstructorViewModel.Save - which THROWS on a duplicate key. A packed
    // form with a repeated id would crash the exam rather than degrade, so refuse it here.
    var dupQuestions = form.Questions.GroupBy(q => q.Id, StringComparer.Ordinal)
        .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (dupQuestions.Count > 0)
        errors.Add($"{rel}: duplicate question ids: {string.Join(", ", dupQuestions)}");

    foreach (var q in form.Questions)
    {
        var dupOptions = q.Options.GroupBy(o => o.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupOptions.Count > 0)
            errors.Add($"{rel}: question '{q.Id}' has duplicate option ids: {string.Join(", ", dupOptions)}");
        if (q.Options.Count == 0)
            errors.Add($"{rel}: question '{q.Id}' has no options");
    }

    forms[form.FormId] = form;
    Console.WriteLine($"  form {form.FormId}: {form.Questions.Count} questions ({form.Specialty}, pass {form.PassFraction:0.##})");
}

if (forms.Count == 0) errors.Add("no readable form under forms/ - the app would show no stations");

// ── Pass 2: answer keys, checked against their form ──────────────────────────────────────────────
var keyCount = 0;
foreach (var (rel, full) in entries.Where(e => e.Rel.StartsWith("answers/", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(e => e.Rel, StringComparer.Ordinal))
{
    if (OskeJson.DeserializeAnswerKey(File.ReadAllText(full)) is not { } key)
    {
        errors.Add($"{rel}: does not parse as an answer key");
        continue;
    }

    var parts = rel.Split('/');
    var ecgFromPath = parts.Length >= 2 ? parts[1] : "";
    var formFromPath = Path.GetFileNameWithoutExtension(rel);
    if (!string.Equals(key.EcgId, ecgFromPath, StringComparison.Ordinal) ||
        !string.Equals(key.FormId, formFromPath, StringComparison.Ordinal))
    {
        errors.Add($"{rel}: key ({key.EcgId}, {key.FormId}) does not match its path - the reader addresses keys by path");
        continue;
    }

    if (!forms.TryGetValue(key.FormId, out var form))
    {
        errors.Add($"{rel}: names form '{key.FormId}', which this dataset does not contain");
        continue;
    }

    // The check this tool exists for: the key must answer exactly the form's blocks.
    var formIds = form.Questions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
    var keyIds = key.CorrectOptionIds.Keys.ToHashSet(StringComparer.Ordinal);
    var missing = formIds.Except(keyIds).ToList();
    var extra = keyIds.Except(formIds).ToList();
    if (missing.Count > 0)
        errors.Add($"{rel}: no answer for form blocks: {string.Join(", ", missing)} " +
                   "(they would grade as correct only for a student who left them blank)");
    if (extra.Count > 0)
        errors.Add($"{rel}: answers blocks the form does not have: {string.Join(", ", extra)} (ignored at grading time)");

    foreach (var q in form.Questions)
    {
        if (!key.CorrectOptionIds.TryGetValue(q.Id, out var correct)) continue;
        var optionIds = q.Options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = correct.Where(id => !optionIds.Contains(id)).ToList();
        if (unknown.Count > 0)
            errors.Add($"{rel}: block '{q.Id}' marks option(s) {string.Join(", ", unknown)} correct, which that block does not offer");
        if (q.Kind == OskeAnswerKind.Single && correct.Count != 1)
            errors.Add($"{rel}: block '{q.Id}' is single-choice but has {correct.Count} correct options");
        if (correct.Count == 0)
            errors.Add($"{rel}: block '{q.Id}' has no correct option");
    }

    keyCount++;
    Console.WriteLine($"  answer key {key.EcgId}/{key.FormId}: {key.CorrectOptionIds.Count} answered blocks");
}

foreach (var formId in forms.Keys.OrderBy(k => k, StringComparer.Ordinal))
    if (!entries.Any(e => e.Rel.EndsWith($"/{formId}.json", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  NOTE: form '{formId}' has no answer key - its specialty card will show 0 stations");

if (errors.Count > 0)
{
    Console.Error.WriteLine($"\n{errors.Count} problem(s) - nothing was packed:");
    foreach (var e in errors) Console.Error.WriteLine($"  - {e}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
var tmp = outPath + ".tmp";
try
{
    using (var file = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        // Dispose order matters: the ZipArchive must write its central directory before the pack
        // stream flushes its final chunk and back-patches the header (see ContentPackWriter).
        using var pack = ContentCrypto.CreateEncryptingWrite(file, leaveOpen: true);
        using var zip = new ZipArchive(pack, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (rel, full) in entries.OrderBy(e => e.Rel, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var src = File.OpenRead(full);
            src.CopyTo(entryStream);
        }
    }

    if (File.Exists(outPath)) File.Delete(outPath);
    File.Move(tmp, outPath);
}
finally
{
    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
}

// Read it back through the very source the app uses, so a bad pack never leaves this tool.
using (var check = EncryptedOskeSource.Open(outPath))
{
    var backForms = check.ReadForms();
    if (backForms.Count != forms.Count)
    {
        Console.Error.WriteLine($"verify FAILED: packed {forms.Count} forms, read back {backForms.Count}");
        return 1;
    }
    var backKeys = backForms.Sum(f => check.ListAnswerKeyEcgIds(f.FormId).Count);
    if (backKeys != keyCount)
    {
        Console.Error.WriteLine($"verify FAILED: packed {keyCount} answer keys, listed {backKeys}");
        return 1;
    }
    foreach (var f in backForms)
        foreach (var ecgId in check.ListAnswerKeyEcgIds(f.FormId))
            if (check.ReadAnswerKey(ecgId, f.FormId) is null)
            {
                Console.Error.WriteLine($"verify FAILED: listed {ecgId}/{f.FormId} but could not read it back");
                return 1;
            }
    Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes), verified {backForms.Count} forms and {backKeys} answer keys");
}

return 0;
