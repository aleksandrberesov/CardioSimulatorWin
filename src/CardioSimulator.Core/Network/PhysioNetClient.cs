using CardioSimulator.Core.Data.Wfdb;

namespace CardioSimulator.Core.Network;

/// <summary>Thrown when a PhysioNet download fails.</summary>
public sealed class PhysioNetException : Exception
{
    public PhysioNetException(string message, Exception? cause = null) : base(message, cause) { }
}

/// <summary>
/// Downloads WFDB records straight from PhysioNet's public file service over HTTPS and decodes them
/// into <see cref="WfdbRecord"/>s, reusing the same parser/codec as on-disk reads.
///
/// PhysioNet exposes published projects under
/// <c>https://physionet.org/files/&lt;project&gt;/&lt;version&gt;/&lt;path&gt;/&lt;record&gt;.hea</c>. For example the
/// Chapman-Shaoxing 12-lead set used by the bundled <c>010/</c> sample lives under the
/// "PhysioNet/CinC Challenge 2021" project. Construct a <see cref="PhysioNetClient"/> and call
/// <see cref="DownloadRecordAsync"/> with the project path (project/version/sub-dirs) and record name.
/// </summary>
public sealed class PhysioNetClient : IDisposable
{
    /// <summary>Base URL of PhysioNet's published-file service.</summary>
    public const string BaseUrl = "https://physionet.org/files/";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>
    /// Creates a client. Pass an existing <paramref name="httpClient"/> to share connection pooling or
    /// to inject a stub in tests; otherwise a default client is created and disposed with this instance.
    /// </summary>
    public PhysioNetClient(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CardioSimulator/1.0 (+WFDB)");
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }
    }

    /// <summary>
    /// Downloads and decodes a single record. <paramref name="projectPath"/> is the directory that
    /// contains the record on PhysioNet, e.g.
    /// <c>"challenge-2021/1.0.3/training/chapman_shaoxing/g1"</c>; <paramref name="record"/> is the base
    /// name without extension, e.g. <c>"JS00001"</c>.
    /// </summary>
    public async Task<WfdbRecord> DownloadRecordAsync(
        string projectPath,
        string record,
        CancellationToken cancellationToken = default)
    {
        var dir = NormalizeDir(projectPath);
        var headerText = await GetStringAsync(dir + record + ".hea", cancellationToken).ConfigureAwait(false);
        var header = WfdbHeaderParser.Parse(headerText) with { RecordName = record };

        // Download each distinct signal file once, then decode entirely in memory.
        var files = header.Signals
            .Select(s => s.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            cache[file] = await GetBytesAsync(dir + file, cancellationToken).ConfigureAwait(false);

        return WfdbReader.ReadRecord(header, name => cache[name]);
    }

    /// <summary>
    /// Downloads a record's files (header + signal files) verbatim into <paramref name="targetDirectory"/>,
    /// preserving the original bytes. Returns the path to the saved <c>.hea</c> file.
    /// </summary>
    public async Task<string> DownloadRecordFilesAsync(
        string projectPath,
        string record,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var dir = NormalizeDir(projectPath);
        Directory.CreateDirectory(targetDirectory);

        var headerText = await GetStringAsync(dir + record + ".hea", cancellationToken).ConfigureAwait(false);
        var header = WfdbHeaderParser.Parse(headerText);

        var headerPath = Path.Combine(targetDirectory, record + ".hea");
        await File.WriteAllTextAsync(headerPath, headerText, cancellationToken).ConfigureAwait(false);

        foreach (var file in header.Signals.Select(s => s.FileName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await GetBytesAsync(dir + file, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(targetDirectory, file), bytes, cancellationToken).ConfigureAwait(false);
        }

        return headerPath;
    }

    /// <summary>
    /// Lists record names from a project's <c>RECORDS</c> index file (one record per line).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRecordsAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var dir = NormalizeDir(projectPath);
        var text = await GetStringAsync(dir + "RECORDS", cancellationToken).ConfigureAwait(false);
        return text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string NormalizeDir(string projectPath)
    {
        var trimmed = projectPath.Trim().Trim('/');
        return BaseUrl + (trimmed.Length == 0 ? "" : trimmed + "/");
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PhysioNetException and not OperationCanceledException)
        {
            throw new PhysioNetException($"Failed to download '{url}'.", ex);
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PhysioNetException and not OperationCanceledException)
        {
            throw new PhysioNetException($"Failed to download '{url}'.", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
