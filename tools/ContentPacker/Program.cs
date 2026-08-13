using System.IO.Compression;
using System.Text;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

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

        case "binarize" when args.Length == 3:
            return Binarize(srcDir: args[1], dstDir: args[2]);

        case "pack-dir" when args.Length is 3 or 5:
            return PackDir(
                srcDir: args[1],
                output: args[2],
                manifestOverride: ManifestOptionValue(args));

        case "repack" when args.Length == 3:
            return Repack(input: args[1], output: args[2]);

        case "apply-acronyms" when args.Length == 4:
            return ApplyAcronyms(input: args[1], mapPath: args[2], output: args[3]);

        case "verify" when args.Length == 2:
            return Verify(pack: args[1]);

        case "cat" when args.Length == 3:
            return Cat(pack: args[1], entry: args[2]);

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

// ── repack: migrate an existing pack to the current container format ─────────────
//
// Copies every entry verbatim from one pack into a new one, so a legacy CSP1 pack becomes a lazily
// readable CSP2 pack without needing the original master data. Entry bytes are untouched — the
// waveforms are byte-for-byte identical, only the container changes.
static int Repack(string input, string output)
{
    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"Input not found: {input}");
        return 1;
    }

    var magic = new byte[4];
    using (var probe = File.OpenRead(input)) { probe.ReadExactly(magic); }
    Console.WriteLine($"Input pack:  {input} ({new FileInfo(input).Length:N0} bytes, {Encoding.ASCII.GetString(magic)})");

    int count;
    // Reading a CSP1 input still costs ~2x its size here; that is the format being escaped, and this
    // is an offline tool on a build machine. The OUTPUT is streamed, so it has no size ceiling.
    using (var src = EncryptedArchive.Open(input))
    using (var packFile = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    using (var packStream = ContentCrypto.CreateEncryptingWrite(packFile, leaveOpen: true))
    {
        using var zip = new ZipArchive(packStream, ZipArchiveMode.Create, leaveOpen: true);
        var paths = src.EntryPaths.ToList();
        count = 0;
        foreach (var path in paths)
        {
            if (src.ReadPath(path) is not { } bytes) continue; // directory entry
            WriteZipEntry(zip, path, bytes);
            count++;
            if (count % 5000 == 0) Console.WriteLine($"  … {count:N0} entries repacked");
        }
    }
    Console.WriteLine($"Wrote pack:  {output} ({new FileInfo(output).Length:N0} bytes, {count:N0} entries)");

    using var verify = EncryptedArchive.Open(output);
    Console.WriteLine($"Verified:    decrypts to {verify.EntryPaths.Count():N0} entries.");
    return 0;
}

// ── apply-acronyms: tag an existing pathology pack with canonical taxonomy acronyms ──
//
// Reads a TSV map (<id>\t<acronym> per line; blank / '#' lines skipped), then writes a new pack in
// which the manifest entries and each named <id>.dat header carry `acronym:`. Every other entry is
// copied verbatim; waveform values are preserved (the .dat is parse→re-serialize round-tripped). Ids
// not in the map are untouched; map ids with no matching .dat are warned about. Reuses the same crypto
// + parser as the runtime, so the output is guaranteed loadable (round-trip verified at the end).
static int ApplyAcronyms(string input, string mapPath, string output)
{
    if (!File.Exists(input)) { Console.Error.WriteLine($"Input not found: {input}"); return 1; }
    if (!File.Exists(mapPath)) { Console.Error.WriteLine($"Map not found: {mapPath}"); return 1; }

    var map = LoadAcronymMap(mapPath);
    Console.WriteLine($"Input pack:  {input} ({new FileInfo(input).Length:N0} bytes)");
    Console.WriteLine($"Acronym map: {map.Count} id→acronym pairs");

    // Streamed one entry at a time so a multi-GB / 45k-record master never lands in memory at once
    // (CSP2 packs decode lazily). Only the manifest is read up front — for its lead order and to write
    // the tagged copy first — everything else is read → tagged → written in a single pass.
    int taggedManifest = 0, taggedDat = 0, processed = 0;
    var seenIds = new HashSet<string>(StringComparer.Ordinal);

    using (var src = EncryptedArchive.Open(input))
    {
        var manifestPath = src.EntryPaths.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("manifest.txt", StringComparison.OrdinalIgnoreCase));
        if (manifestPath is null || src.ReadPath(manifestPath) is not { } manifestBytes)
        {
            Console.Error.WriteLine("Input pack has no manifest.txt");
            return 1;
        }
        var manifest = PathologyParser.ParseManifest(Encoding.UTF8.GetString(manifestBytes));
        IReadOnlyList<Lead> leadOrder = manifest.LeadOrder.Count > 0 ? manifest.LeadOrder : Leads.All;

        var tagPath = output;
        using var packFile = new FileStream(tagPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var packStream = ContentCrypto.CreateEncryptingWrite(packFile, leaveOpen: true);
        using var zip = new ZipArchive(packStream, ZipArchiveMode.Create, leaveOpen: true);

        // Tagged manifest first.
        var updatedEntries = manifest.Entries
            .Select(e => map.TryGetValue(e.Id, out var a) ? e with { Acronyms = a } : e)
            .ToList();
        taggedManifest = updatedEntries.Count(e => e.AcronymList.Count > 0);
        WriteZipEntry(zip, manifestPath,
            new UTF8Encoding(false).GetBytes(PathologyParser.SerializeManifest(manifest with { Entries = updatedEntries })));

        // Then stream every other entry, tagging .dat headers as they pass through.
        foreach (var path in src.EntryPaths)
        {
            if (path == manifestPath) continue;
            if (src.ReadPath(path) is not { } data) continue; // directory entry
            var payload = data;
            var name = Path.GetFileName(path);

            if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                var id = name[..^4];
                seenIds.Add(id);
                if (map.TryGetValue(id, out var acr))
                {
                    var file = PathologyParser.ParsePathology(data) with { Acronyms = acr };
                    // Preserve the record's original encoding. Some records have samples outside the
                    // 16-bit range and were kept as TEXT (CSD1 can't delta-encode them) — re-serialize
                    // those as text, and binary records as binary, so no record ever fails to encode.
                    payload = PathologyParser.HasBinaryMagic(data)
                        ? PathologyParser.SerializePathologyBytes(file, leadOrder)
                        : new UTF8Encoding(false).GetBytes(PathologyParser.SerializePathology(file, leadOrder));
                    taggedDat++;
                }
            }
            WriteZipEntry(zip, path, payload);
            if (++processed % 5000 == 0) Console.WriteLine($"  … {processed:N0} entries streamed");
        }
    }

    var mapIdsMissing = map.Keys.Count(id => !seenIds.Contains(id));
    if (mapIdsMissing > 0)
        Console.WriteLine($"Note: {mapIdsMissing:N0} of {map.Count:N0} map ids are not in this pack (expected when applying a master map to a subset).");
    Console.WriteLine($"Tagged {taggedManifest} manifest entries, {taggedDat} .dat headers.");
    Console.WriteLine($"Wrote pack:  {output} ({new FileInfo(output).Length:N0} bytes)");

    // Round-trip verify via the exact runtime read path.
    using var verify = EncryptedPathologySource.Open(output);
    var vm = verify.ReadManifest();
    var withAcronym = vm?.Entries.Count(e => e.AcronymList.Count > 0) ?? 0;
    Console.WriteLine($"Verified:    {withAcronym} manifest entries carry an acronym; {verify.ListPathologies().Count} .dat entries total.");
    return 0;
}

// id → acronym list. The TSV value may be a single code or a comma-separated list ("SB,LVH,TWC");
// the first is the primary diagnosis.
static Dictionary<string, IReadOnlyList<string>> LoadAcronymMap(string path)
{
    var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        var parts = line.Split('\t');
        if (parts.Length < 2) continue;
        var id = parts[0].Trim();
        var acronyms = parts[1].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (id.Length > 0 && acronyms.Count > 0) map[id] = acronyms;
    }
    return map;
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

    // Compile plaintext .dat waveforms to the compact delta-binary (CSD1) format inside the ZIP
    // before encrypting. Course packs (no .dat entries) pass through byte-for-byte.
    zipBytes = ConvertPathologyDatEntries(zipBytes);

    // Stream the (already in-memory) ZIP out as a CSP2 pack.
    using (var packFile = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    using (var packStream = ContentCrypto.CreateEncryptingWrite(packFile, leaveOpen: true))
    {
        packStream.Write(zipBytes, 0, zipBytes.Length);
    }
    Console.WriteLine($"Wrote pack:  {output} ({new FileInfo(output).Length:N0} bytes)");

    // Round-trip verify: prove the runtime path can open what we just wrote.
    using var archive = EncryptedArchive.Open(output);
    var verified = archive.EntryPaths.Count();
    Console.WriteLine($"Verified:    decrypts to {verified} entries.");
    return 0;
}

// ── binarize: text .dat master → binary .dat master (one file at a time) ─────────
//
// Compiles a loose pathology dataset directory to the CSD1 delta-binary format, writing a parallel
// directory. Each .dat is converted independently so memory use is constant regardless of dataset
// size (the full ~45k-record set never lives in memory at once). manifest.txt / groups.txt and any
// other files are copied verbatim; an already-binary .dat is copied as-is (idempotent).
static int Binarize(string srcDir, string dstDir)
{
    if (!Directory.Exists(srcDir))
    {
        Console.Error.WriteLine($"Source directory not found: {srcDir}");
        return 1;
    }
    if (string.Equals(Path.GetFullPath(srcDir), Path.GetFullPath(dstDir), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Destination must differ from source.");
        return 1;
    }
    Directory.CreateDirectory(dstDir);

    var files = Directory.EnumerateFiles(srcDir);
    int converted = 0, copied = 0, kept = 0;
    long rawIn = 0, rawOut = 0;
    foreach (var path in files)
    {
        var name = Path.GetFileName(path);
        var dst = Path.Combine(dstDir, name);
        if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            var data = File.ReadAllBytes(path);
            if (LooksBinary(data))
            {
                File.WriteAllBytes(dst, data); // already CSD1 — copy as-is
                copied++;
                continue;
            }
            try
            {
                var file = PathologyParser.ParsePathology(data);
                var binary = PathologyParser.SerializePathologyBytes(file, file.Leads.Keys.ToList());
                File.WriteAllBytes(dst, binary);
                converted++;
                rawIn += data.Length;
                rawOut += binary.Length;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  WARN: keeping '{name}' as text ({ex.Message})");
                File.WriteAllBytes(dst, data);
                kept++;
            }
        }
        else
        {
            File.Copy(path, dst, overwrite: true); // manifest.txt, groups.txt, aux files
            copied++;
        }

        var done = converted + kept;
        if (done > 0 && done % 5000 == 0)
        {
            Console.WriteLine($"  … {done:N0} .dat processed");
        }
    }

    Console.WriteLine(
        $"Binarized {converted:N0} .dat ({kept} kept as text, {copied} copied verbatim). " +
        $"Uncompressed .dat {rawIn:N0} -> {rawOut:N0} bytes.");
    Console.WriteLine($"Output: {dstDir}");
    return 0;
}

// ── pack-dir: build an encrypted pak straight from a loose (binary) directory ────
//
// Streams the chosen entries into a temporary ZIP on disk (so datasets far larger than 2 GB never
// need a single in-memory buffer), then encrypts that ZIP into the output pak. The set of pathologies
// is defined by a manifest: --manifest <file> selects a subset, otherwise the directory's own
// manifest.txt is used. Only <id>.dat files named by a `pathology:` line are included; groups.txt and
// the manifest itself travel with them. A .dat that is still plain text is compiled to CSD1 on the
// fly, so pack-dir is correct whether or not `binarize` was run first.
static int PackDir(string srcDir, string output, string? manifestOverride)
{
    if (!Directory.Exists(srcDir))
    {
        Console.Error.WriteLine($"Source directory not found: {srcDir}");
        return 1;
    }

    var manifestPath = manifestOverride ?? Path.Combine(srcDir, "manifest.txt");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"Manifest not found: {manifestPath}");
        return 1;
    }

    var manifestText = File.ReadAllText(manifestPath, new UTF8Encoding(false));
    var ids = ManifestPathologyIds(manifestText);
    if (ids.Count == 0)
    {
        Console.Error.WriteLine("Manifest lists no pathologies.");
        return 1;
    }
    Console.WriteLine($"Source dir:  {srcDir}");
    Console.WriteLine($"Manifest:    {manifestPath} ({ids.Count:N0} pathologies)");

    int written = 0, converted = 0, missing = 0;
    long rawTotal = 0;

    // Zip AND encrypt in one streaming pass straight into the .pak. There is no temp plaintext ZIP
    // on disk and no whole-dataset buffer in memory at any point: each .dat is deflated into a CSP2
    // chunk and encrypted as it goes.
    using (var packFile = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    using (var packStream = ContentCrypto.CreateEncryptingWrite(packFile, leaveOpen: true))
    {
        using var zip = new ZipArchive(packStream, ZipArchiveMode.Create, leaveOpen: true);

        // manifest.txt (the selected one) and groups.txt travel with the data.
        WriteZipEntry(zip, "manifest.txt", new UTF8Encoding(false).GetBytes(manifestText));
        var groupsPath = Path.Combine(srcDir, "groups.txt");
        if (File.Exists(groupsPath))
        {
            WriteZipEntry(zip, "groups.txt", File.ReadAllBytes(groupsPath));
        }

        foreach (var id in ids)
        {
            var datPath = Path.Combine(srcDir, id + ".dat");
            if (!File.Exists(datPath))
            {
                missing++;
                if (missing <= 10) Console.Error.WriteLine($"  WARN: missing {id}.dat");
                continue;
            }
            var data = File.ReadAllBytes(datPath);
            if (!LooksBinary(data))
            {
                // Compile text → CSD1 on the fly (pack-dir works on a text master too).
                try
                {
                    var file = PathologyParser.ParsePathology(data);
                    data = PathologyParser.SerializePathologyBytes(file, file.Leads.Keys.ToList());
                    converted++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  WARN: {id}.dat kept as text ({ex.Message})");
                }
            }
            WriteZipEntry(zip, id + ".dat", data);
            rawTotal += data.Length;
            written++;
            if (written % 5000 == 0) Console.WriteLine($"  … {written:N0} .dat packed");
        }
    }

    if (missing > 10) Console.Error.WriteLine($"  WARN: {missing:N0} .dat total were missing.");
    Console.WriteLine(
        $"Packed {written:N0} .dat ({converted:N0} compiled on the fly); " +
        $"uncompressed {rawTotal:N0} bytes -> {new FileInfo(output).Length:N0} bytes encrypted.");

    // Round-trip verify: prove the runtime path can open what we just wrote.
    using var archive = EncryptedArchive.Open(output);
    Console.WriteLine($"Verified:    decrypts to {archive.EntryPaths.Count():N0} entries.");
    return 0;
}

// --manifest <file> may appear as args[3]/args[4]; returns the value or null.
static string? ManifestOptionValue(string[] args) =>
    args.Length == 5 && string.Equals(args[3], "--manifest", StringComparison.OrdinalIgnoreCase)
        ? args[4]
        : null;

// True if the bytes begin with the CSD1 magic (already delta-binary).
static bool LooksBinary(byte[] data) =>
    data.Length >= 4 && data[0] == (byte)'C' && data[1] == (byte)'S' && data[2] == (byte)'D' && data[3] == (byte)'1';

// Ids named by `pathology:<id>` manifest lines, in manifest order, de-duplicated.
static List<string> ManifestPathologyIds(string manifestText)
{
    var ids = new List<string>();
    var seen = new HashSet<string>();
    foreach (var raw in manifestText.Split('\n'))
    {
        var line = raw.Trim();
        if (!line.StartsWith("pathology:", StringComparison.Ordinal)) continue;
        var body = line.Substring("pathology:".Length);
        var semi = body.IndexOf(';');
        var id = (semi >= 0 ? body.Substring(0, semi) : body).Trim();
        if (id.Length > 0 && seen.Add(id)) ids.Add(id);
    }
    return ids;
}

static void WriteZipEntry(ZipArchive zip, string name, byte[] data)
{
    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
    using var s = entry.Open();
    s.Write(data, 0, data.Length);
}

// Rewrites a content ZIP so each plaintext <id>.dat becomes the compact CSD1 delta-binary. Packs
// with no .dat entries (e.g. course packs) are returned untouched so their bytes never change. A
// .dat that fails to convert (e.g. an out-of-range sample) is kept as text — the runtime reader
// auto-detects each entry's format, so a mixed pack still loads.
static byte[] ConvertPathologyDatEntries(byte[] zipBytes)
{
    var entries = new List<(string Name, byte[] Data)>();
    var hasDat = false;
    using (var src = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read))
    {
        foreach (var e in src.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream((int)Math.Max(0, e.Length));
            s.CopyTo(ms);
            entries.Add((e.FullName, ms.ToArray()));
            if (e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) hasDat = true;
        }
    }

    if (!hasDat) return zipBytes;

    // Lead order is cosmetic for round-tripping (the parser keys leads by name), but honor the
    // manifest's order when available so binary blocks match the source file's layout.
    IReadOnlyList<Lead> leadOrder = Leads.All;
    var manifest = entries.FirstOrDefault(x =>
        Path.GetFileName(x.Name).Equals("manifest.txt", StringComparison.OrdinalIgnoreCase));
    if (manifest.Data is not null)
    {
        try
        {
            var parsed = PathologyParser.ParseManifest(Encoding.UTF8.GetString(manifest.Data));
            if (parsed.LeadOrder.Count > 0) leadOrder = parsed.LeadOrder;
        }
        catch { /* keep canonical order */ }
    }

    int converted = 0, kept = 0;
    long rawBefore = 0, rawAfter = 0;
    using var outMs = new MemoryStream();
    using (var dst = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, data) in entries)
        {
            var payload = data;
            if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var file = PathologyParser.ParsePathology(data);
                    payload = PathologyParser.SerializePathologyBytes(file, leadOrder);
                    converted++;
                    rawBefore += data.Length;
                    rawAfter += payload.Length;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  WARN: keeping '{name}' as text ({ex.Message})");
                    kept++;
                }
            }

            var entry = dst.CreateEntry(name, CompressionLevel.SmallestSize);
            using var es = entry.Open();
            es.Write(payload, 0, payload.Length);
        }
    }

    Console.WriteLine(
        $"Delta-binary: converted {converted} .dat ({kept} kept as text); " +
        $"uncompressed .dat {rawBefore:N0} -> {rawAfter:N0} bytes.");
    return outMs.ToArray();
}

// Prints one decrypted entry (e.g. manifest.txt) to stdout — handy for inspecting a pack's metadata.
static int Cat(string pack, string entry)
{
    if (!File.Exists(pack)) { Console.Error.WriteLine($"Pack not found: {pack}"); return 1; }
    using var src = EncryptedArchive.Open(pack);
    if (src.ReadPath(entry) is not { } bytes) { Console.Error.WriteLine($"Entry not found: {entry}"); return 1; }
    Console.Out.Write(Encoding.UTF8.GetString(bytes));
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
    Console.Error.WriteLine("  pack     <input.zip> <output.pak>              Encrypt input.zip into output.pak");
    Console.Error.WriteLine("  binarize <srcDir> <dstDir>                     Compile a loose text .dat dataset to CSD1 binary");
    Console.Error.WriteLine("  pack-dir <srcDir> <output.pak> [--manifest F]  Encrypt a loose (binary) dataset dir into a pak");
    Console.Error.WriteLine("  repack <input.pak> <output.pak>                Migrate a pack to the current container (CSP1 -> CSP2)");
    Console.Error.WriteLine("  apply-acronyms <in.pak> <map.tsv> <out.pak>    Tag manifest + .dat headers with taxonomy acronyms");
    Console.Error.WriteLine("  verify   <input.pak>                           Decrypt and list the pack's entries");
    Console.Error.WriteLine("  inspect-pathologies <input.pak>                Parse a pathology pack via the runtime read path");
    Console.Error.WriteLine("  inspect-courses     <input.pak>                Parse a course pack via the runtime read path");
}
