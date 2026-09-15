using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CardioSimulator.Core.Network;

/// <summary>Which way a <see cref="TcpTrafficEntry"/> travelled.</summary>
public enum TcpTrafficDirection
{
    /// <summary>App → server (a JSON frame or a raw upload payload).</summary>
    Outgoing,

    /// <summary>Server → app (one reply line).</summary>
    Incoming,

    /// <summary>A connection-lifecycle or delivery event; nothing crossed the wire.</summary>
    Event,
}

/// <summary>The kind of an <see cref="TcpTrafficDirection.Event"/> entry.</summary>
public enum TcpTrafficEvent
{
    /// <summary>Not an event (used by <see cref="TcpTrafficDirection.Outgoing"/>/<see cref="TcpTrafficDirection.Incoming"/> entries).</summary>
    None,

    /// <summary>A connect attempt started.</summary>
    Connecting,

    /// <summary>The socket connected.</summary>
    Connected,

    /// <summary>A connect attempt failed (refused, unreachable, timed out); the app retries.</summary>
    ConnectFailed,

    /// <summary>A live connection ended (peer closed it or the socket failed); the app retries.</summary>
    Disconnected,

    /// <summary>The user switched the link off.</summary>
    UserDisconnect,

    /// <summary>Writing a frame to the socket failed.</summary>
    SendFailed,

    /// <summary>A <c>query</c> got no reply in time; the app sent the rhythm anyway (fail-open).</summary>
    ReplyTimeout,

    /// <summary>The server sent too many bytes without a newline; the receive buffer was discarded.</summary>
    ReceiveOverflow,

    /// <summary>A message the app wanted to send was skipped (not connected, no data).</summary>
    NotSent,
}

/// <summary>
/// One row of the <see cref="TcpTrafficLog"/>: a frame sent to / received from the monitor server, or a
/// connection event. Immutable; <see cref="Payload"/> is a bounded preview, never the whole multi-MB frame.
/// </summary>
public sealed record TcpTrafficEntry
{
    /// <summary>Monotonically increasing per log, starting at 1. Never reset — not even by <see cref="TcpTrafficLog.Clear"/>.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the entry was recorded (the log's clock, local time by default).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    public required TcpTrafficDirection Direction { get; init; }

    /// <summary>The event kind for <see cref="TcpTrafficDirection.Event"/> entries; <see cref="TcpTrafficEvent.None"/> otherwise.</summary>
    public TcpTrafficEvent Event { get; init; }

    /// <summary>
    /// Wire token: <c>upload</c>, <c>query</c>, <c>rhythm</c>, <c>start</c>, <c>stop</c>, <c>points</c>, <c>payload</c>
    /// (outgoing); <c>OK</c>, <c>no_data</c>, <c>status</c>, <c>ack</c>, <c>unknown</c> (incoming); the
    /// <see cref="TcpTrafficEvent"/> name for events.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>One-line technical summary in the protocol's (English) vocabulary.</summary>
    public required string Summary { get; init; }

    /// <summary>Raw text preview: at most <see cref="TcpTrafficLog.MaxBulkPayloadChars"/> characters for the bulk kinds
    /// (<c>rhythm</c>/<c>points</c> frames, upload payloads, reply lines), <see cref="TcpTrafficLog.MaxPayloadChars"/> otherwise.</summary>
    public required string Payload { get; init; }

    /// <summary>True when <see cref="Payload"/> is only a prefix of what was sent/received.</summary>
    public bool PayloadTruncated { get; init; }

    /// <summary>Bytes on the wire including the trailing <c>\n</c> of a frame; 0 for events.</summary>
    public long ByteCount { get; init; }

    public bool IsError { get; init; }

    /// <summary>The outgoing message's <c>id</c>, or the <c>id</c> the server echoed, when present.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Running totals since the log was created or last <see cref="TcpTrafficLog.Clear">cleared</see>.
/// Unaffected by ring-buffer trimming. Upload payload bytes count toward <see cref="SentBytes"/> but not
/// <see cref="SentMessages"/>.</summary>
public readonly record struct TcpTrafficStats(long SentMessages, long SentBytes, long ReceivedMessages, long ReceivedBytes, long Errors);

/// <summary>
/// Always-on, bounded record of the TCP server conversation for the administrator's "Server message log"
/// window. The app records every frame it writes, every reply line it reads and every connection event;
/// the window loads <see cref="Snapshot"/> and then follows <see cref="EntryAdded"/>.
///
/// <para>Recording sits on the network path, so it is built never to disturb it: no method throws for
/// any input, a record is O(1) amortized plus a bounded preview (a multi-MB <c>rhythm</c> line or upload
/// payload is never copied or decoded past <see cref="MaxBulkPayloadChars"/>), and subscriber exceptions are
/// swallowed. Thread-safe.</para>
/// </summary>
public sealed class TcpTrafficLog
{
    public const int DefaultCapacity = 2000;
    public const int DefaultMaxPayloadChars = 16_000;

    /// <summary>Default preview cap for the bulk kinds. A 2k-char prefix still identifies the frame (type, id,
    /// pathology, sample rate, the first samples); the rest is numeric noise nobody reads, and at 16k chars a full
    /// log of such rows would pin tens of MB for the life of the process.</summary>
    public const int DefaultMaxBulkPayloadChars = 2048;

    /// <summary>How much of an unrecognized reply line is echoed into its summary.</summary>
    private const int MaxEchoChars = 200;

    /// <summary>Hard cap on any summary, so an odd rhythm name or server id can't make a multi-KB list row.</summary>
    private const int MaxSummaryChars = 400;

    /// <summary>Longest single value (name, id, filename) quoted into a summary.</summary>
    private const int MaxTokenChars = 120;

    private const string Unrecognized = "unrecognized line (ignored by app)";

    // _gate guards the buffer, the stats and the sequence. _publishGate is taken outside it and serializes
    // "mutate + notify", so EntryAdded/Cleared reach subscribers in exactly the order the state changed
    // (a UI that dedupes by Sequence never drops a row) while Snapshot/Count/Stats never wait on a subscriber.
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly Queue<TcpTrafficEntry> _entries;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;
    private TcpTrafficStats _stats;

    /// <param name="capacity">Entries kept; the oldest are dropped beyond it. Clamped to at least 1.</param>
    /// <param name="maxPayloadChars">Longest <see cref="TcpTrafficEntry.Payload"/> preview. Clamped to at least 1.</param>
    /// <param name="clock">Timestamp source (tests inject one); defaults to <see cref="DateTimeOffset.Now"/>.</param>
    /// <param name="maxBulkPayloadChars">Longest preview for the bulk kinds (see <see cref="MaxBulkPayloadChars"/>).
    /// Clamped to 1…<see cref="MaxPayloadChars"/>: it only ever tightens the general cap.</param>
    public TcpTrafficLog(int capacity = DefaultCapacity, int maxPayloadChars = DefaultMaxPayloadChars, Func<DateTimeOffset>? clock = null,
                         int maxBulkPayloadChars = DefaultMaxBulkPayloadChars)
    {
        Capacity = Math.Max(1, capacity);
        MaxPayloadChars = Math.Max(1, maxPayloadChars);
        MaxBulkPayloadChars = Math.Clamp(maxBulkPayloadChars, 1, MaxPayloadChars);
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _entries = new Queue<TcpTrafficEntry>(Math.Min(Capacity, 256));
    }

    public int Capacity { get; }

    /// <summary>Preview cap for events and every outgoing frame that is not a bulk kind (<c>upload</c> header,
    /// <c>query</c>, <c>start</c>, <c>stop</c>).</summary>
    public int MaxPayloadChars { get; }

    /// <summary>Preview cap for the bulk kinds: outgoing <c>rhythm</c> and <c>points</c> frames, upload payloads
    /// (<see cref="RecordOutgoingPayload"/>) and reply lines (<see cref="RecordIncoming"/>). Never above
    /// <see cref="MaxPayloadChars"/>.</summary>
    public int MaxBulkPayloadChars { get; }

    /// <summary>Entries currently held (at most <see cref="Capacity"/>).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public TcpTrafficStats Stats
    {
        get { lock (_gate) return _stats; }
    }

    /// <summary>
    /// Raised synchronously on the <b>recording</b> thread (often a thread-pool socket thread) after the
    /// internal state lock is released. Deliveries are serialized and arrive in <see cref="TcpTrafficEntry.Sequence"/>
    /// order; each subscriber runs in its own try/catch, so a throwing subscriber can't break the TCP path or
    /// the other subscribers. Keep handlers short and non-blocking — UI subscribers must marshal to their
    /// dispatcher (enqueue, never wait on the UI thread).
    /// </summary>
    public event Action<TcpTrafficEntry>? EntryAdded;

    /// <summary>Raised after <see cref="Clear"/>, under the same rules as <see cref="EntryAdded"/>.</summary>
    public event Action? Cleared;

    /// <summary>A copy of the held entries, oldest first.</summary>
    public IReadOnlyList<TcpTrafficEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>
    /// Records a JSON frame about to be written. <paramref name="encodedLine"/> is the already-encoded JSON
    /// (<see cref="TcpProtocol.Encode"/>); <see cref="TcpTrafficEntry.ByteCount"/> is its UTF-8 size plus the
    /// <c>\n</c> terminator. A line passed with its terminator already appended is counted the same way.
    /// Counts one sent message.
    /// </summary>
    public TcpTrafficEntry RecordOutgoing(TcpMessage message, string encodedLine)
    {
        var line = encodedLine ?? string.Empty;
        var hasTerminator = line.EndsWith('\n');
        // GetByteCount walks the string without allocating; the preview below is the only copy, and bounded.
        var byteCount = (long)Encoding.UTF8.GetByteCount(line) + (hasTerminator ? 0 : 1);
        // rhythm/points frames carry the samples (multi-MB for a rhythm), so they get the smaller bulk cap.
        var maxChars = message is TcpMessage.RhythmMessage or TcpMessage.PointsMessage ? MaxBulkPayloadChars : MaxPayloadChars;
        var (preview, truncated) = Preview(line, hasTerminator ? line.Length - 1 : line.Length, maxChars);

        var draft = new TcpTrafficEntry
        {
            Sequence = 0,
            Timestamp = default,
            Direction = TcpTrafficDirection.Outgoing,
            Kind = message is null ? "unknown" : SafeType(message),
            Summary = Summarize(message!),
            Payload = preview,
            PayloadTruncated = truncated,
            ByteCount = byteCount,
            CorrelationId = message?.Id,
        };
        return Append(draft, new TcpTrafficStats(1, byteCount, 0, 0, 0));
    }

    /// <summary>
    /// Records the raw bytes that follow an <c>upload</c> header (e.g. <c>manifest.txt</c>). The preview is a
    /// UTF-8 decode of a bounded prefix only. Adds to <see cref="TcpTrafficStats.SentBytes"/> but is not a message.
    /// </summary>
    public TcpTrafficEntry RecordOutgoingPayload(string filename, ReadOnlySpan<byte> payload)
    {
        var (preview, truncated) = DecodePreview(payload, MaxBulkPayloadChars);
        var name = filename ?? string.Empty;
        var summary = name.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"payload {Token(name)} bytes={payload.Length}")
            : string.Create(CultureInfo.InvariantCulture, $"payload bytes={payload.Length}");

        var draft = new TcpTrafficEntry
        {
            Sequence = 0,
            Timestamp = default,
            Direction = TcpTrafficDirection.Outgoing,
            Kind = "payload",
            Summary = OneLine(summary, MaxSummaryChars),
            Payload = preview,
            PayloadTruncated = truncated,
            ByteCount = payload.Length,
        };
        return Append(draft, new TcpTrafficStats(0, payload.Length, 0, 0, 0));
    }

    /// <summary>
    /// Records one reply line read from the server (<paramref name="byteCount"/> = bytes consumed including
    /// the <c>\n</c>). Classified by <see cref="ClassifyIncoming"/>. Counts one received message.
    /// </summary>
    public TcpTrafficEntry RecordIncoming(string line, long byteCount)
    {
        var text = line ?? string.Empty;
        var (kind, summary, correlationId) = ClassifyIncoming(text);
        var (preview, truncated) = Preview(text, text.Length, MaxBulkPayloadChars);
        var bytes = Math.Max(0, byteCount);

        var draft = new TcpTrafficEntry
        {
            Sequence = 0,
            Timestamp = default,
            Direction = TcpTrafficDirection.Incoming,
            Kind = kind,
            Summary = summary,
            Payload = preview,
            PayloadTruncated = truncated,
            ByteCount = bytes,
            CorrelationId = correlationId,
        };
        return Append(draft, new TcpTrafficStats(0, 0, 1, bytes, 0));
    }

    /// <summary>
    /// Records a connection/delivery event. <see cref="TcpTrafficEntry.Summary"/> and <see cref="TcpTrafficEntry.Payload"/>
    /// both carry <paramref name="detail"/> (the summary flattened to one line). Counts an error when
    /// <paramref name="isError"/>.
    /// </summary>
    public TcpTrafficEntry RecordEvent(TcpTrafficEvent kind, string? detail = null, bool isError = false)
    {
        var text = detail ?? string.Empty;
        var (preview, truncated) = Preview(text, text.Length, MaxPayloadChars);

        var draft = new TcpTrafficEntry
        {
            Sequence = 0,
            Timestamp = default,
            Direction = TcpTrafficDirection.Event,
            Event = kind,
            Kind = kind.ToString(),
            Summary = OneLine(text, MaxSummaryChars),
            Payload = preview,
            PayloadTruncated = truncated,
            IsError = isError,
        };
        return Append(draft, new TcpTrafficStats(0, 0, 0, 0, isError ? 1 : 0));
    }

    /// <summary>Drops every entry and resets <see cref="Stats"/> (not <see cref="TcpTrafficEntry.Sequence"/>), then raises <see cref="Cleared"/>.</summary>
    public void Clear()
    {
        lock (_publishGate)
        {
            lock (_gate)
            {
                _entries.Clear();
                _stats = default;
            }

            var handlers = Cleared;
            if (handlers is null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch { /* a broken subscriber must not break the caller or its siblings */ }
            }
        }
    }

    /// <summary>
    /// A compact, invariant-culture one-liner for an outgoing message, e.g.
    /// <c>query pathology=ecg42200 hash=3af9c1e0b2d4e5f6</c> or
    /// <c>rhythm pathology=ecg42200 sampleRate=500 leads=I:5000,II:5000 (2 leads, 10000 samples)</c>.
    /// Values with spaces are quoted. Never throws; null yields an empty string.
    /// </summary>
    public static string Summarize(TcpMessage message)
    {
        if (message is null) return string.Empty;
        try
        {
            var summary = message switch
            {
                TcpMessage.UploadMessage upload =>
                    string.Create(CultureInfo.InvariantCulture, $"upload {Token(upload.Filename)} size={upload.Size}"),
                TcpMessage.QueryCommand query => query.Hash is null
                    ? "query pathology=" + Token(query.Pathology)
                    : "query pathology=" + Token(query.Pathology) + " hash=" + Token(query.Hash),
                TcpMessage.RhythmMessage rhythm => SummarizeRhythm(rhythm),
                TcpMessage.StartCommand start => SummarizeStart(start),
                TcpMessage.StopCommand => TcpMessage.StopCommand.TypeName,
                TcpMessage.PointsMessage points => SummarizePoints(points),
                TcpMessage.AckMessage ack =>
                    string.Create(CultureInfo.InvariantCulture, $"ack {Token(ack.Filename)} bytes={ack.Bytes}"),
                _ => SafeType(message),
            };
            return OneLine(summary, MaxSummaryChars);
        }
        catch
        {
            return SafeType(message);
        }
    }

    /// <summary>
    /// Classifies a server reply line exactly the way <c>AppViewModel.HandleReplyLine</c> parses it:
    /// bare <c>OK</c> / <c>no_data</c> / <c>nodata</c> (case-insensitive), a JSON <c>{"id":…,"status":"ok"|"no_data"}</c>
    /// verdict, an <c>ack</c>, or anything else (which the app ignores). A verdict's summary states only what the
    /// server said, never what the app did next: the reply may resolve a superseded query, arrive after the
    /// fail-open send, or match no pending query at all. Long lines are cut in the summary. Never throws.
    /// </summary>
    public static (string Kind, string Summary, string? CorrelationId) ClassifyIncoming(string line)
    {
        try
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return ("OK", "OK — server has it cached", null);
            if (text.Equals("no_data", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("nodata", StringComparison.OrdinalIgnoreCase))
                return ("no_data", "no_data — server does not have it", null);
            if (!text.StartsWith('{'))
                return ("unknown", UnrecognizedSummary(text), null);

            string? id = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = idEl.GetString();
                    // Status wins over type, mirroring the app: any object carrying a verdict is a verdict.
                    if (root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                    {
                        var st = stEl.GetString();
                        if (string.Equals(st, "ok", StringComparison.OrdinalIgnoreCase))
                            return ("status", StatusSummary("ok", id, "(server has it cached)"), id);
                        if (string.Equals(st, "no_data", StringComparison.OrdinalIgnoreCase))
                            return ("status", StatusSummary("no_data", id, "(server does not have it)"), id);
                    }
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String &&
                        typeEl.GetString() == TcpMessage.AckMessage.TypeName)
                        return ("ack", AckSummary(root), id);
                }
            }
            catch { /* malformed JSON — the app ignores it too */ }
            return ("unknown", UnrecognizedSummary(text), id);
        }
        catch
        {
            return ("unknown", Unrecognized, null);
        }
    }

    /// <summary>Human byte size, 1024-based, invariant culture: <c>0 B</c>, <c>812 B</c>, <c>7.9 KB</c>, <c>1.24 MB</c>.</summary>
    public static string FormatBytes(long bytes)
    {
        var inv = CultureInfo.InvariantCulture;
        if (bytes < 1024) return bytes.ToString(inv) + " B";
        // Thresholds sit just under 1024 at the displayed precision so a value never renders as "1024.0 KB".
        var kb = bytes / 1024.0;
        if (kb < 1023.95) return kb.ToString("0.0", inv) + " KB";
        var mb = kb / 1024.0;
        if (mb < 1023.995) return mb.ToString("0.00", inv) + " MB";
        var gb = mb / 1024.0;
        if (gb < 1023.995) return gb.ToString("0.00", inv) + " GB";
        return (gb / 1024.0).ToString("0.00", inv) + " TB";
    }

    // ---------------------------------------------------------------------------------------------------

    /// <summary>Stamps the draft with sequence + time, stores it, updates stats and notifies — in that order.</summary>
    private TcpTrafficEntry Append(TcpTrafficEntry draft, TcpTrafficStats delta)
    {
        lock (_publishGate)
        {
            TcpTrafficEntry entry;
            lock (_gate)
            {
                entry = draft with { Sequence = ++_sequence, Timestamp = Now() };
                _entries.Enqueue(entry);
                while (_entries.Count > Capacity) _entries.Dequeue();
                _stats = new TcpTrafficStats(
                    _stats.SentMessages + delta.SentMessages,
                    _stats.SentBytes + delta.SentBytes,
                    _stats.ReceivedMessages + delta.ReceivedMessages,
                    _stats.ReceivedBytes + delta.ReceivedBytes,
                    _stats.Errors + delta.Errors);
            }

            var handlers = EntryAdded;
            if (handlers is not null)
            {
                foreach (var d in handlers.GetInvocationList())
                {
                    try { ((Action<TcpTrafficEntry>)d)(entry); }
                    catch { /* a broken subscriber must never break the TCP path or its siblings */ }
                }
            }
            return entry;
        }
    }

    private DateTimeOffset Now()
    {
        try { return _clock(); }
        catch { return DateTimeOffset.Now; }
    }

    /// <summary>The first <paramref name="length"/> chars of <paramref name="source"/>, cut to <paramref name="maxChars"/>
    /// (≥ 1) without splitting a surrogate pair. Returns <paramref name="source"/> itself when nothing is cut.</summary>
    private static (string Text, bool Truncated) Preview(string source, int length, int maxChars)
    {
        if (length <= maxChars)
            return (length == source.Length ? source : source.Substring(0, length), false);
        var cut = maxChars;
        if (char.IsHighSurrogate(source[cut - 1])) cut--;
        return (source.Substring(0, cut), true);
    }

    /// <summary>UTF-8 decodes at most <paramref name="maxChars"/> (≥ 1) chars from the start of a binary payload.</summary>
    private static (string Text, bool Truncated) DecodePreview(ReadOnlySpan<byte> payload, int maxChars)
    {
        if (payload.IsEmpty) return (string.Empty, false);
        try
        {
            // A UTF-16 char never takes more than 3 UTF-8 bytes (a 4-byte sequence yields 2 chars) and an invalid
            // byte still yields a U+FFFD, so this prefix always holds enough input to fill the char buffer: the
            // rest of a multi-MB payload is never touched. Decoding N bytes never yields more than N chars.
            var prefixBytes = (int)Math.Min(payload.Length, (long)maxChars * 3 + 4);
            var whole = prefixBytes == payload.Length;
            var chars = new char[Math.Min(maxChars, prefixBytes)];
            var decoder = Encoding.UTF8.GetDecoder();
            decoder.Convert(payload[..prefixBytes], chars, flush: whole, out var bytesUsed, out var charsUsed, out var completed);
            var truncated = !(whole && completed && bytesUsed == prefixBytes);
            return (new string(chars, 0, charsUsed), truncated);
        }
        catch
        {
            // e.g. a 1-char buffer facing a surrogate pair — show nothing rather than fail the send path.
            return (string.Empty, true);
        }
    }

    private static string SafeType(TcpMessage message)
    {
        try { return message.Type ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string SummarizeRhythm(TcpMessage.RhythmMessage rhythm)
    {
        var sb = new StringBuilder("rhythm pathology=").Append(Token(rhythm.Pathology));
        if (rhythm.SampleRate is int rate) sb.Append(CultureInfo.InvariantCulture, $" sampleRate={rate}");

        var leadCount = 0;
        long samples = 0;
        if (rhythm.Leads is { Count: > 0 } leads)
        {
            sb.Append(" leads=");
            // Canonical lead order, so the summary is stable regardless of dictionary order.
            foreach (var (lead, data) in leads.OrderBy(kv => kv.Key))
            {
                var n = data?.Length ?? 0;
                if (leadCount++ > 0) sb.Append(',');
                sb.Append(CultureInfo.InvariantCulture, $"{lead}:{n}");
                samples += n;
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $" ({leadCount} {(leadCount == 1 ? "lead" : "leads")}, {samples} samples)");
        return sb.ToString();
    }

    private static string SummarizeStart(TcpMessage.StartCommand start)
    {
        var sb = new StringBuilder(TcpMessage.StartCommand.TypeName);
        if (start.Params is { Count: > 0 } ps)
        {
            if (ps.TryGetValue("pathology", out var pathology)) sb.Append(" pathology=").Append(Token(pathology));
            if (ps.TryGetValue("name", out var name)) sb.Append(" name=").Append(Token(name));
            foreach (var (key, value) in ps.Where(kv => kv.Key is not ("pathology" or "name"))
                                           .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.Append(' ').Append(Token(key)).Append('=').Append(Token(value));
            }
        }
        if (start.SampleRate is int rate) sb.Append(CultureInfo.InvariantCulture, $" sampleRate={rate}");
        return sb.ToString();
    }

    private static string SummarizePoints(TcpMessage.PointsMessage points)
    {
        var sb = new StringBuilder(TcpMessage.PointsMessage.TypeName);
        if (points.Lead is { } lead) sb.Append(" lead=").Append(lead.ToString());
        if (points.Identy is not null) sb.Append(" identy=").Append(Token(points.Identy));
        sb.Append(CultureInfo.InvariantCulture, $" offset={points.Offset} values={points.Values?.Count ?? 0}");
        return sb.ToString();
    }

    private static string StatusSummary(string status, string? id, string verdict) =>
        id is null
            ? "status=" + status + " " + verdict
            : "status=" + status + " id=" + Token(id) + " " + verdict;

    private static string AckSummary(JsonElement root)
    {
        var sb = new StringBuilder(TcpMessage.AckMessage.TypeName);
        if (root.TryGetProperty("filename", out var fileEl) && fileEl.ValueKind == JsonValueKind.String)
            sb.Append(' ').Append(Token(fileEl.GetString()));
        if (root.TryGetProperty("bytes", out var bytesEl) && bytesEl.ValueKind == JsonValueKind.Number)
        {
            if (bytesEl.TryGetInt64(out var whole))
                sb.Append(CultureInfo.InvariantCulture, $" bytes={whole}");
            else if (bytesEl.TryGetDouble(out var fractional))
                sb.Append(CultureInfo.InvariantCulture, $" bytes={fractional}");
        }
        return OneLine(sb.ToString(), MaxSummaryChars);
    }

    private static string UnrecognizedSummary(string text) =>
        text.Length == 0 ? Unrecognized : Unrecognized + ": " + OneLine(text, MaxEchoChars);

    /// <summary>A summary-safe value: as-is when plain, else quoted with <c>"</c> and <c>\</c> escaped; long values are cut.</summary>
    private static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Length > MaxTokenChars) value = OneLine(value, MaxTokenChars);
        var plain = true;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '=' or '\\')
            {
                plain = false;
                break;
            }
        }
        return plain ? value : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Flattens control chars (newlines, tabs) to spaces and cuts to <paramref name="maxChars"/> with an ellipsis,
    /// scanning no further than the cut. Returns <paramref name="text"/> itself when it is already clean and short.</summary>
    private static string OneLine(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var cut = text.Length > maxChars;
        var length = cut ? maxChars : text.Length;
        if (cut && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        var span = text.AsSpan(0, length);

        var clean = true;
        foreach (var c in span)
        {
            if (char.IsControl(c))
            {
                clean = false;
                break;
            }
        }
        if (clean && !cut) return text;

        var sb = new StringBuilder(length + 1);
        foreach (var c in span) sb.Append(char.IsControl(c) ? ' ' : c);
        if (cut) sb.Append('…');
        return sb.ToString();
    }
}
