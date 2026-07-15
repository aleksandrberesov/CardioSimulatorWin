using System.IO.Compression;
using CardioSimulator.Core.Data;

// Offline packer for the encrypted content packs. See ContentPacker.csproj for usage.

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    PrintUsage();
    return 2;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "pack" when args.Length == 3:
            return Pack(input: args[1], output: args[2]);

        case "verify" when args.Length == 2:
            return Verify(pack: args[1]);

        case "inspect-pathologies" when args.Length == 2:
            return InspectPathologies(pack: args[1]);

        case "inspect-courses" when args.Length == 2:
            return InspectCourses(pack: args[1]);

        default:
            PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Pack(string input, string output)
{
    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"Input not found: {input}");
        return 1;
    }

    var zipBytes = File.ReadAllBytes(input);

    // Sanity-check that the input really is a ZIP before we encrypt it, so a mistake produces a
    // clear error now rather than an unreadable pack at runtime.
    try
    {
        using var probe = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var count = probe.Entries.Count;
        Console.WriteLine($"Input ZIP: {input} ({zipBytes.Length:N0} bytes, {count} entries)");
    }
    catch
    {
        Console.Error.WriteLine($"Input is not a valid ZIP: {input}");
        return 1;
    }

    var pack = ContentCrypto.Encrypt(zipBytes);
    File.WriteAllBytes(output, pack);
    Console.WriteLine($"Wrote pack:  {output} ({pack.Length:N0} bytes)");

    // Round-trip verify: prove the runtime path can open what we just wrote.
    using var archive = EncryptedArchive.Open(output);
    var verified = archive.EntryPaths.Count();
    Console.WriteLine($"Verified:    decrypts to {verified} entries.");
    return 0;
}

static int Verify(string pack)
{
    if (!File.Exists(pack))
    {
        Console.Error.WriteLine($"Pack not found: {pack}");
        return 1;
    }

    using var archive = EncryptedArchive.Open(pack);
    var entries = archive.EntryPaths.OrderBy(e => e).ToList();
    Console.WriteLine($"{pack}: {entries.Count} entries");
    foreach (var e in entries)
    {
        Console.WriteLine($"  {e}");
    }
    return 0;
}

// Exercises the exact runtime read path (EncryptedArchive -> EncryptedPathologySource -> parser)
// so a pack can be validated end-to-end without launching the app.
static int InspectPathologies(string pack)
{
    if (!File.Exists(pack))
    {
        Console.Error.WriteLine($"Pack not found: {pack}");
        return 1;
    }

    using var source = EncryptedPathologySource.Open(pack);
    var manifest = source.ReadManifest();
    if (manifest is null)
    {
        Console.Error.WriteLine("FAILED: pack has no readable manifest.txt");
        return 1;
    }

    var ids = source.ListPathologies();
    Console.WriteLine($"manifest entries: {manifest.Entries.Count}");
    Console.WriteLine($".dat files:       {ids.Count}");
    Console.WriteLine($"groups.txt:       {(source.ReadGroupsText() is { Length: > 0 } ? "present" : "absent")}");

    var sampleId = ids.FirstOrDefault();
    if (sampleId is not null)
    {
        var file = source.ReadPathology(sampleId);
        Console.WriteLine(file is null
            ? $"FAILED: could not parse sample pathology '{sampleId}'"
            : $"sample '{sampleId}': {file.Leads.Count} leads parsed OK");
        if (file is null) return 1;
    }
    return 0;
}

// Exercises the runtime course read path (EncryptedArchive -> EncryptedCourseSource -> parser)
// including a coursehost asset fetch, without launching the app.
static int InspectCourses(string pack)
{
    if (!File.Exists(pack))
    {
        Console.Error.WriteLine($"Pack not found: {pack}");
        return 1;
    }

    using var source = EncryptedCourseSource.Open(pack);
    var manifest = source.ReadManifest();
    if (manifest is null)
    {
        Console.Error.WriteLine("FAILED: pack has no readable manifest.txt");
        return 1;
    }

    var courses = source.ListCourses();
    Console.WriteLine($"manifest entries: {manifest.Entries.Count}");
    Console.WriteLine($"courses:          {courses.Count} [{string.Join(", ", courses)}]");

    var courseId = courses.FirstOrDefault();
    if (courseId is not null)
    {
        var course = source.ReadCourse(courseId);
        var lectures = source.ListLectures(courseId);
        Console.WriteLine($"course '{courseId}': parsed={(course is not null)}, lectures={lectures.Count}");

        var lectureId = lectures.FirstOrDefault();
        if (lectureId is not null)
        {
            var lecture = source.ReadLecture(courseId, lectureId, "en");
            Console.WriteLine($"lecture '{lectureId}.en': {(lecture is null ? "MISSING" : $"{lecture.RawHtml.Length} chars")}");
            if (lecture is null) return 1;
        }

        // Prove a coursehost asset resolves (the WebView serves these from memory).
        var asset = source.ReadAsset(courseId, "assets/heart.svg");
        Console.WriteLine($"asset assets/heart.svg: {(asset is null ? "absent" : $"{asset.Length} bytes")}");
    }
    return 0;
}

static void PrintUsage()
{
    Console.Error.WriteLine("ContentPacker — encrypt a content ZIP into a distributable *.pak");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  pack   <input.zip> <output.pak>   Encrypt input.zip into output.pak");
    Console.Error.WriteLine("  verify <input.pak>                Decrypt and list the pack's entries");
    Console.Error.WriteLine("  inspect-pathologies <input.pak>   Parse a pathology pack via the runtime read path");
    Console.Error.WriteLine("  inspect-courses     <input.pak>   Parse a course pack via the runtime read path");
}
