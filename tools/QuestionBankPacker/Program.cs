using System.IO.Compression;
using CardioSimulator.Core.Data;

// Packs a question-bank interchange JSON into the encrypted pack the app ships as
// Assets/QuestionBank.pak. Usage:
//
//   dotnet run --project tools/QuestionBankPacker -- <source.json> <out.pak>
//
// The source is the array format the Test Constructor imports and exports (and that
// TestJson.DeserializeBank reads). The pack holds it as one entry, EncryptedQuestionBankSource
// .BankEntryPath, because reading several thousand per-question files costs tens of seconds on a
// machine with real-time AV scanning while the single packed array parses in well under a second.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: QuestionBankPacker <source.json> <out.pak>");
    return 2;
}

var sourcePath = args[0];
var outPath = args[1];

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"source not found: {sourcePath}");
    return 2;
}

var json = File.ReadAllText(sourcePath);

// Parse before packing: a pack that cannot be read back is worse than no pack, and the count is the
// number the Test Constructor will show, so it is worth printing at build time.
var questions = TestJson.DeserializeBank(json);
if (questions.Count == 0)
{
    Console.Error.WriteLine("source parsed to zero questions - wrong format?");
    return 1;
}

var missingId = questions.Count(q => string.IsNullOrEmpty(q.Id));
var themes = questions.Select(q => q.Theme).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
Console.WriteLine($"read {questions.Count} questions, {themes} themes" + (missingId > 0 ? $", {missingId} WITHOUT an id (skipped at runtime)" : ""));

// Re-serialize rather than packing the source bytes verbatim: this stores exactly what the runtime
// will read back, so a schema drift between the customer's export and this build fails here rather
// than silently dropping fields on the user's machine.
var canonical = TestJson.SerializeBank(questions);

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
        var entry = zip.CreateEntry(EncryptedQuestionBankSource.BankEntryPath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(canonical);
    }

    if (File.Exists(outPath)) File.Delete(outPath);
    File.Move(tmp, outPath);
}
finally
{
    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
}

// Read it back through the very source the app uses, so a bad pack never leaves this tool.
using (var check = EncryptedQuestionBankSource.Open(outPath))
{
    var back = check.ReadQuestions();
    if (back.Count != questions.Count(q => !string.IsNullOrEmpty(q.Id)))
    {
        Console.Error.WriteLine($"verify FAILED: packed {questions.Count}, read back {back.Count}");
        return 1;
    }
    Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length:N0} bytes), verified {back.Count} questions");
}

return 0;
