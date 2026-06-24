using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CardioSimulator.App.Data;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Network;

/// <summary>
/// The Group-mode classroom server: a tiny self-contained HTTP/1.1 server (raw <see cref="TcpListener"/>,
/// <c>Connection: close</c>) that lets students on the same Wi-Fi take an individually-generated test in
/// their phone browser. It binds <c>0.0.0.0:&lt;port&gt;</c> directly (so — unlike
/// <see cref="HttpListener"/> — it needs no <c>netsh</c> URL ACL or admin rights). The teacher shows a QR
/// to <see cref="Url"/>; each phone registers, gets an answer-free quiz (<see cref="QuizDto"/>), submits,
/// and the attempt is graded (<see cref="ExamGrader"/>) and saved to the shared report
/// (<see cref="ExamResultStore"/>) — the same store the on-screen Examination uses.
/// </summary>
/// <remarks>
/// Runs only while a session is active (<see cref="Start"/>/<see cref="Stop"/>). Answers / comments are
/// never serialized to clients. HTTP-only on the LAN (fine for a classroom; browsers flag "not secure").
/// ECG-stimulus questions show as text on phones (no Win2D trace there).
/// </remarks>
public sealed class GroupTestServer
{
    public sealed class Participant
    {
        public required string Token { get; init; }
        public required ExamStudentInfo Student { get; init; }
        public required Test Test { get; init; }
        public ExamResult? Result { get; set; }
        public DateTimeOffset RegisteredAt { get; init; }
        public bool Finished => Result is not null;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<IReadOnlyList<TestQuestion>> _bank;
    private readonly ExamResultStore _resultStore;
    private readonly ConcurrentDictionary<string, Participant> _participants = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public GroupTestServer(Func<IReadOnlyList<TestQuestion>> bank, ExamResultStore resultStore)
    {
        _bank = bank;
        _resultStore = resultStore;
    }

    public int Count { get; private set; }
    public string? Theme { get; private set; }
    public int Port { get; private set; }
    public string? Url { get; private set; }
    public bool IsRunning => _listener is not null;

    /// <summary>Snapshot of the current participants (registered + finished), newest registration last.</summary>
    public IReadOnlyList<Participant> Participants =>
        _participants.Values.OrderBy(p => p.RegisteredAt).ToList();

    /// <summary>Raised (on a background thread) whenever a student registers or submits — the UI marshals
    /// it to the dispatcher to refresh the roster.</summary>
    public event Action? ParticipantsChanged;

    /// <summary>Starts a session and returns the URL to encode in the QR, or null if no LAN IP / port is
    /// available. <paramref name="count"/> questions per student, optionally limited to <paramref name="theme"/>.</summary>
    public string? Start(int count, string? theme, int preferredPort = 8080)
    {
        Stop();
        var ip = LocalIPv4();
        if (ip is null) return null;

        var listener = TryListen(preferredPort, out var port);
        if (listener is null) return null;

        Count = count;
        Theme = theme;
        Port = port;
        Url = $"http://{ip}:{port}/";
        _participants.Clear();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Url;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _cts = null;
        Url = null;
    }

    // ── Accept / handle ─────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; } // listener stopped / cancelled
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                using var stream = client.GetStream();
                var req = await ReadRequestAsync(stream, ct);
                if (req is null) return;
                var (status, contentType, body) = Route(req.Value.method, req.Value.path, req.Value.query, req.Value.body);
                await WriteResponseAsync(stream, status, contentType, body, ct);
            }
            catch { /* drop the connection on any parse/IO error */ }
        }
    }

    private static async Task<(string method, string path, Dictionary<string, string> query, byte[] body)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new List<byte>();
        int headerEnd = -1;

        // Read until the blank line that ends the headers.
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) return null;
            for (var i = 0; i < n; i++) acc.Add(buffer[i]);
            headerEnd = IndexOfDoubleCrlf(acc);
            if (acc.Count > 1_000_000) return null; // guard
        }

        var all = acc.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        var method = parts[0];
        var rawUrl = parts[1];

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
        }

        var (path, query) = SplitUrl(rawUrl);

        // Body: bytes already past the header terminator, plus any remaining up to Content-Length.
        var bodyStart = headerEnd + 4;
        using var body = new MemoryStream();
        var have = all.Length - bodyStart;
        if (have > 0) body.Write(all, bodyStart, Math.Min(have, contentLength > 0 ? contentLength : have));
        while (body.Length < contentLength)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) break;
            body.Write(buffer, 0, n);
        }
        return (method, path, query, body.ToArray());
    }

    private (int status, string contentType, byte[] body) Route(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body)
    {
        if (method == "GET" && (path == "/" || path == "/index.html"))
            return (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(GroupQuizPage.Html));

        if (method == "GET" && path == "/favicon.ico")
            return (204, "image/x-icon", Array.Empty<byte>());

        if (method == "POST" && path == "/api/register")
            return Register(body);

        if (method == "GET" && path == "/api/image")
            return Image(query);

        if (method == "POST" && path == "/api/submit")
            return Submit(body);

        return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
    }

    private sealed record RegisterReq(string? FullName, string? Group);
    private sealed record SubmitReq(string? Token, Dictionary<string, string>? Selections);

    private (int, string, byte[]) Register(byte[] body)
    {
        RegisterReq? req;
        try { req = JsonSerializer.Deserialize<RegisterReq>(body, Json); } catch { req = null; }
        var fio = req?.FullName?.Trim();
        var grp = req?.Group?.Trim();
        if (string.IsNullOrEmpty(fio) || string.IsNullOrEmpty(grp))
            return Error(400, "missing name/group");

        var test = TestGenerator.Generate(_bank(), Count, Theme, Random.Shared);
        var token = Guid.NewGuid().ToString("N");
        _participants[token] = new Participant
        {
            Token = token,
            Student = new ExamStudentInfo(fio, grp),
            Test = test,
            RegisteredAt = DateTimeOffset.Now,
        };
        ParticipantsChanged?.Invoke();

        var payload = JsonSerializer.Serialize(new
        {
            token,
            title = test.Title,
            questions = QuizDto.ToPublic(test),
        }, Json);
        return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(payload));
    }

    private (int, string, byte[]) Image(IReadOnlyDictionary<string, string> query)
    {
        if (!query.TryGetValue("token", out var token) || !query.TryGetValue("qid", out var qid) ||
            !_participants.TryGetValue(token, out var p))
            return Error(404, "no image");

        var q = p.Test.Questions.FirstOrDefault(x => x.Id == qid);
        if (q?.Stimulus != QuestionStimulus.Image || string.IsNullOrEmpty(q.ImagePath) || !TestImageStore.Exists(q.ImagePath))
            return Error(404, "no image");

        try
        {
            var bytes = File.ReadAllBytes(TestImageStore.FullPath(q.ImagePath));
            return (200, ContentTypeFor(q.ImagePath), bytes);
        }
        catch { return Error(404, "no image"); }
    }

    private (int, string, byte[]) Submit(byte[] body)
    {
        SubmitReq? req;
        try { req = JsonSerializer.Deserialize<SubmitReq>(body, Json); } catch { req = null; }
        if (req?.Token is null || !_participants.TryGetValue(req.Token, out var p))
            return Error(404, "unknown token");

        if (p.Result is null)
        {
            var selections = req.Selections ?? new Dictionary<string, string>();
            var result = ExamGrader.Grade(p.Test, selections, p.Student);
            _resultStore.Save(result);
            p.Result = result;
            ParticipantsChanged?.Invoke();
        }

        var payload = JsonSerializer.Serialize(new
        {
            correct = p.Result!.CorrectCount,
            total = p.Result.TotalCount,
            passed = p.Result.Passed,
        }, Json);
        return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(payload));
    }

    private static (int, string, byte[]) Error(int status, string message) =>
        (status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));

    // ── HTTP plumbing ─────────────────────────────────────────────────────────

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        var head =
            $"HTTP/1.1 {status} {Reason(status)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        _ => "OK",
    };

    private static int IndexOfDoubleCrlf(List<byte> buf)
    {
        for (var i = 0; i + 3 < buf.Count; i++)
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        return -1;
    }

    private static (string path, Dictionary<string, string> query) SplitUrl(string rawUrl)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qm = rawUrl.IndexOf('?');
        var path = qm < 0 ? rawUrl : rawUrl[..qm];
        if (qm >= 0)
        {
            foreach (var pair in rawUrl[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) q[Uri.UnescapeDataString(pair)] = "";
                else q[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return (path, q);
    }

    private static string ContentTypeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => "application/octet-stream",
    };

    private static TcpListener? TryListen(int preferredPort, out int port)
    {
        foreach (var p in new[] { preferredPort, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start();
                port = ((IPEndPoint)l.LocalEndpoint).Port;
                return l;
            }
            catch { /* try the next candidate */ }
        }
        port = 0;
        return null;
    }

    /// <summary>The machine's LAN IPv4 (preferring Wi-Fi / Ethernet), or null when offline.</summary>
    public static string? LocalIPv4()
    {
        IPAddress? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue; // APIPA
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
                    return ua.Address.ToString();
                fallback ??= ua.Address;
            }
        }
        return fallback?.ToString();
    }
}
