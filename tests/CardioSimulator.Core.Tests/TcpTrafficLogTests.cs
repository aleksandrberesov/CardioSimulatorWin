using System.Text;
using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Network;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TcpTrafficLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 10, 30, 0, TimeSpan.Zero);

    /// <summary>A deterministic clock: T0, T0+1 ms, T0+2 ms, … one tick per call.</summary>
    private static Func<DateTimeOffset> SteppingClock()
    {
        var calls = 0;
        return () => T0.AddMilliseconds(calls++);
    }

    private static TcpTrafficLog NewLog(int capacity = TcpTrafficLog.DefaultCapacity,
                                        int maxPayloadChars = TcpTrafficLog.DefaultMaxPayloadChars,
                                        int maxBulkPayloadChars = TcpTrafficLog.DefaultMaxBulkPayloadChars) =>
        new(capacity, maxPayloadChars, SteppingClock(), maxBulkPayloadChars);

    private static readonly TcpMessage Stop = new TcpMessage.StopCommand { Id = "s1" };
    private const string StopLine = "{\"type\":\"stop\",\"id\":\"s1\"}";

    // ---- Summarize ---------------------------------------------------------------------------------

    [Fact]
    public void Summarize_Upload()
    {
        var msg = new TcpMessage.UploadMessage { Id = "u1", Filename = "manifest.txt", Size = 8123 };
        Assert.Equal("upload manifest.txt size=8123", TcpTrafficLog.Summarize(msg));
    }

    [Fact]
    public void Summarize_Query_WithAndWithoutHash()
    {
        Assert.Equal("query pathology=ecg42200 hash=3af9c1e0b2d4e5f6",
            TcpTrafficLog.Summarize(new TcpMessage.QueryCommand { Pathology = "ecg42200", Hash = "3af9c1e0b2d4e5f6" }));
        Assert.Equal("query pathology=ecg42200",
            TcpTrafficLog.Summarize(new TcpMessage.QueryCommand { Pathology = "ecg42200" }));
    }

    [Fact]
    public void Summarize_Query_WithRevision()
    {
        Assert.Equal("query pathology=ecg42200 revision=0 hash=3af9c1e0b2d4e5f6",
            TcpTrafficLog.Summarize(new TcpMessage.QueryCommand
            {
                Pathology = "ecg42200",
                Hash = "3af9c1e0b2d4e5f6",
                Revision = "0",
            }));
    }

    [Fact]
    public void Summarize_Rhythm_WithRevision()
    {
        Assert.Equal("rhythm pathology=p1 revision=3af9c1e0 leads=II:3 (1 lead, 3 samples)",
            TcpTrafficLog.Summarize(new TcpMessage.RhythmMessage
            {
                Pathology = "p1",
                Revision = "3af9c1e0",
                Leads = new Dictionary<Lead, int[]> { [Lead.II] = new[] { 1, 2, 3 } },
            }));
    }

    [Fact]
    public void Summarize_Time()
    {
        Assert.Equal("time datetime=2026-09-17T13:35:12.345+03:00",
            TcpTrafficLog.Summarize(new TcpMessage.TimeMessage { Datetime = "2026-09-17T13:35:12.345+03:00" }));
    }

    [Fact]
    public void Summarize_Rhythm_ListsLeadsInCanonicalOrderWithTotals()
    {
        var msg = new TcpMessage.RhythmMessage
        {
            Pathology = "ecg42200",
            SampleRate = 500,
            // Deliberately out of canonical order.
            Leads = new Dictionary<Lead, int[]>
            {
                [Lead.V3] = new int[5000],
                [Lead.I] = new int[5000],
                [Lead.II] = new int[4000],
            },
        };
        Assert.Equal("rhythm pathology=ecg42200 sampleRate=500 leads=I:5000,II:4000,V3:5000 (3 leads, 14000 samples)",
            TcpTrafficLog.Summarize(msg));
    }

    [Fact]
    public void Summarize_Rhythm_SingleLead_NoSampleRate_AndEmpty()
    {
        Assert.Equal("rhythm pathology=p1 leads=II:3 (1 lead, 3 samples)",
            TcpTrafficLog.Summarize(new TcpMessage.RhythmMessage
            {
                Pathology = "p1",
                Leads = new Dictionary<Lead, int[]> { [Lead.II] = new[] { 1, 2, 3 } },
            }));
        Assert.Equal("rhythm pathology=p1 (0 leads, 0 samples)",
            TcpTrafficLog.Summarize(new TcpMessage.RhythmMessage { Pathology = "p1" }));
    }

    [Fact]
    public void Summarize_Rhythm_NullLeadsAndNullSamples_DoNotThrow()
    {
        Assert.Equal("rhythm pathology=p1 (0 leads, 0 samples)",
            TcpTrafficLog.Summarize(new TcpMessage.RhythmMessage { Pathology = "p1", Leads = null! }));
        Assert.Equal("rhythm pathology=p1 leads=I:0 (1 lead, 0 samples)",
            TcpTrafficLog.Summarize(new TcpMessage.RhythmMessage
            {
                Pathology = "p1",
                Leads = new Dictionary<Lead, int[]> { [Lead.I] = null! },
            }));
    }

    [Fact]
    public void Summarize_Start_QuotesNameAndPutsPathologyFirst()
    {
        var msg = new TcpMessage.StartCommand
        {
            SampleRate = 500,
            Params = new Dictionary<string, string>
            {
                ["zeta"] = "1",
                ["name"] = "Sinus rhythm",
                ["pathology"] = "ecg42200",
                ["alpha"] = "x",
            },
        };
        Assert.Equal("start pathology=ecg42200 name=\"Sinus rhythm\" alpha=x zeta=1 sampleRate=500",
            TcpTrafficLog.Summarize(msg));
    }

    [Fact]
    public void Summarize_Start_Minimal()
    {
        Assert.Equal("start", TcpTrafficLog.Summarize(new TcpMessage.StartCommand()));
    }

    [Fact]
    public void Summarize_Stop()
    {
        Assert.Equal("stop", TcpTrafficLog.Summarize(new TcpMessage.StopCommand { Id = "m2" }));
    }

    [Fact]
    public void Summarize_Points()
    {
        Assert.Equal("points lead=II offset=0 values=250",
            TcpTrafficLog.Summarize(new TcpMessage.PointsMessage { Lead = Lead.II, Values = new float[250] }));
        Assert.Equal("points identy=series-1 offset=10 values=0",
            TcpTrafficLog.Summarize(new TcpMessage.PointsMessage { Identy = "series-1", Offset = 10 }));
    }

    [Fact]
    public void Summarize_Ack()
    {
        Assert.Equal("ack manifest.txt bytes=8123",
            TcpTrafficLog.Summarize(new TcpMessage.AckMessage { Filename = "manifest.txt", Bytes = 8123 }));
    }

    [Fact]
    public void Summarize_Null_IsEmpty()
    {
        Assert.Equal("", TcpTrafficLog.Summarize(null!));
    }

    [Fact]
    public void Summarize_FlattensNewlinesAndCapsLength()
    {
        var msg = new TcpMessage.StartCommand
        {
            Params = new Dictionary<string, string> { ["name"] = "line1\nline2" + new string('x', 10_000) },
        };
        var summary = TcpTrafficLog.Summarize(msg);
        Assert.DoesNotContain("\n", summary);
        Assert.True(summary.Length <= 400, $"summary length {summary.Length}");
    }

    // ---- ClassifyIncoming --------------------------------------------------------------------------

    [Theory]
    [InlineData("OK")]
    [InlineData("ok")]
    [InlineData("  Ok \r")]
    public void ClassifyIncoming_BareOk(string line)
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.Equal("OK", kind);
        Assert.Equal("OK — server has it cached", summary);
        Assert.Null(id);
    }

    [Theory]
    [InlineData("no_data")]
    [InlineData("NO_DATA")]
    [InlineData("nodata")]
    public void ClassifyIncoming_BareNoData(string line)
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.Equal("no_data", kind);
        Assert.Equal("no_data — server does not have it", summary);
        Assert.Null(id);
    }

    [Fact]
    public void ClassifyIncoming_JsonStatusOk_WithId()
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming("{\"id\":\"q-17\",\"status\":\"ok\"}");
        Assert.Equal("status", kind);
        Assert.Equal("status=ok id=q-17 (server has it cached)", summary);
        Assert.Equal("q-17", id);
    }

    [Fact]
    public void ClassifyIncoming_JsonStatusNoData_WithId_CaseInsensitive()
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming("{\"status\":\"NO_DATA\",\"id\":\"q-18\"}");
        Assert.Equal("status", kind);
        Assert.Equal("status=no_data id=q-18 (server does not have it)", summary);
        Assert.Equal("q-18", id);
    }

    [Fact]
    public void ClassifyIncoming_JsonStatus_WithoutIdOrNonStringId()
    {
        Assert.Equal(("status", "status=ok (server has it cached)", (string?)null),
            TcpTrafficLog.ClassifyIncoming("{\"status\":\"ok\"}"));
        Assert.Equal(("status", "status=no_data (server does not have it)", (string?)null),
            TcpTrafficLog.ClassifyIncoming("{\"id\":7,\"status\":\"no_data\"}"));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("nodata")]
    [InlineData("{\"id\":\"q1\",\"status\":\"ok\"}")]
    [InlineData("{\"status\":\"no_data\"}")]
    public void ClassifyIncoming_VerdictSummaries_StateOnlyTheServerVerdict(string line)
    {
        // The reply may resolve a superseded query or arrive after the fail-open send, so the summary must not
        // claim what the app did next.
        var (_, summary, _) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.DoesNotContain("send", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sent", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyIncoming_JsonNodataStatus_IsNotAVerdict_LikeTheApp()
    {
        // The app accepts "nodata" only as a bare token, not as a JSON status.
        var (kind, _, id) = TcpTrafficLog.ClassifyIncoming("{\"id\":\"q1\",\"status\":\"nodata\"}");
        Assert.Equal("unknown", kind);
        Assert.Equal("q1", id);
    }

    [Fact]
    public void ClassifyIncoming_BareAck_IsAConfirmation_LikeTheApp()
    {
        // The app completes a waiting rhythm/start on a bare "ack" exactly as on "OK"; the log must not call it
        // an unrecognised line the app ignored.
        Assert.Equal(("ack", "ack — server confirmed", (string?)null), TcpTrafficLog.ClassifyIncoming("ack"));
        Assert.Equal("ack", TcpTrafficLog.ClassifyIncoming("  ACK ").Kind);
    }

    [Fact]
    public void ClassifyIncoming_Ack()
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(
            "{\"type\":\"ack\",\"id\":\"u1\",\"filename\":\"manifest.txt\",\"bytes\":8123}");
        Assert.Equal("ack", kind);
        Assert.Equal("ack manifest.txt bytes=8123", summary);
        Assert.Equal("u1", id);
    }

    [Fact]
    public void ClassifyIncoming_UploadJsonAck_ClassifiedAsAck()
    {
        var json = "{\"uid\":null,\"type\":\"upload\",\"id\":\"46befbbf-4d80-454c-8f5f-628822b1d21a\",\"filename\":\"manifest.txt\",\"status\":null,\"size\":119538}";
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(json);
        Assert.Equal("ack", kind);
        Assert.Equal("ack manifest.txt bytes=119538", summary);
        Assert.Equal("46befbbf-4d80-454c-8f5f-628822b1d21a", id);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"status\":\"ok\"")]
    [InlineData("{\"type\":\"foo\"}")]
    [InlineData("hello server")]
    [InlineData("[1,2,3]")]
    public void ClassifyIncoming_MalformedOrOther_IsUnknown(string line)
    {
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.Equal("unknown", kind);
        Assert.StartsWith("unrecognized line (ignored by app)", summary);
        Assert.Contains(line.Trim(), summary);
        Assert.Null(id);
    }

    [Fact]
    public void ClassifyIncoming_UnknownJson_KeepsIdIfAny()
    {
        var (kind, _, id) = TcpTrafficLog.ClassifyIncoming("{\"type\":\"hello\",\"id\":\"x9\"}");
        Assert.Equal("unknown", kind);
        Assert.Equal("x9", id);
    }

    [Fact]
    public void ClassifyIncoming_EmptyAndNull()
    {
        Assert.Equal(("unknown", "unrecognized line (ignored by app)", (string?)null), TcpTrafficLog.ClassifyIncoming(""));
        Assert.Equal(("unknown", "unrecognized line (ignored by app)", (string?)null), TcpTrafficLog.ClassifyIncoming(null!));
    }

    [Fact]
    public void ClassifyIncoming_VeryLongGarbageLine_SummaryIsCut()
    {
        var line = new string('x', 100_000);
        var (kind, summary, _) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.Equal("unknown", kind);
        Assert.True(summary.Length < 300, $"summary length {summary.Length}");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void ClassifyIncoming_VeryLongJsonVerdict_StillClassified()
    {
        var line = "{\"id\":\"q1\",\"pad\":\"" + new string('p', 100_000) + "\",\"status\":\"ok\"}";
        var (kind, summary, id) = TcpTrafficLog.ClassifyIncoming(line);
        Assert.Equal("status", kind);
        Assert.Equal("q1", id);
        Assert.True(summary.Length < 300);
    }

    // ---- Recording, capacity, sequence, timestamps --------------------------------------------------

    [Fact]
    public void Record_StampsSequenceAndInjectedClock()
    {
        var log = NewLog();
        var a = log.RecordEvent(TcpTrafficEvent.Connecting, "10.0.0.5:8080");
        var b = log.RecordOutgoing(Stop, StopLine);
        var c = log.RecordIncoming("OK", 3);

        Assert.Equal(new long[] { 1, 2, 3 }, new[] { a.Sequence, b.Sequence, c.Sequence });
        Assert.Equal(T0, a.Timestamp);
        Assert.Equal(T0.AddMilliseconds(1), b.Timestamp);
        Assert.Equal(T0.AddMilliseconds(2), c.Timestamp);
        Assert.Equal(new[] { a, b, c }, log.Snapshot());
    }

    [Fact]
    public void RecordOutgoing_FillsEntry()
    {
        var log = NewLog();
        var msg = new TcpMessage.QueryCommand { Id = "q1", Pathology = "ecg42200", Hash = "abcd" };
        var line = TcpProtocol.Encode(msg);

        var e = log.RecordOutgoing(msg, line);

        Assert.Equal(TcpTrafficDirection.Outgoing, e.Direction);
        Assert.Equal(TcpTrafficEvent.None, e.Event);
        Assert.Equal("query", e.Kind);
        Assert.Equal("query pathology=ecg42200 hash=abcd", e.Summary);
        Assert.Equal(line, e.Payload);
        Assert.False(e.PayloadTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(line) + 1, e.ByteCount);
        Assert.Equal("q1", e.CorrelationId);
        Assert.False(e.IsError);
    }

    [Fact]
    public void RecordOutgoing_ByteCountIsUtf8_AndToleratesTrailingNewline()
    {
        var log = NewLog();
        var msg = new TcpMessage.StartCommand { Params = new Dictionary<string, string> { ["name"] = "Синус" } };
        // Hand-written: TcpProtocol.Encode would \u-escape the Cyrillic and make the line pure ASCII.
        const string line = "{\"type\":\"start\",\"params\":{\"name\":\"Синус\"}}";
        var expected = Encoding.UTF8.GetByteCount(line) + 1;
        Assert.True(expected > line.Length + 1, "test needs multi-byte chars");

        Assert.Equal(expected, log.RecordOutgoing(msg, line).ByteCount);
        var withTerminator = log.RecordOutgoing(msg, line + "\n");
        Assert.Equal(expected, withTerminator.ByteCount);
        Assert.Equal(line, withTerminator.Payload);
    }

    [Fact]
    public void RecordIncoming_FillsEntry()
    {
        var log = NewLog();
        var e = log.RecordIncoming("{\"id\":\"q1\",\"status\":\"no_data\"}", 33);

        Assert.Equal(TcpTrafficDirection.Incoming, e.Direction);
        Assert.Equal("status", e.Kind);
        Assert.Equal("q1", e.CorrelationId);
        Assert.Equal("{\"id\":\"q1\",\"status\":\"no_data\"}", e.Payload);
        Assert.Equal(33, e.ByteCount);
    }

    [Fact]
    public void RecordEvent_FillsEntry_AndFlattensSummary()
    {
        var log = NewLog();
        var e = log.RecordEvent(TcpTrafficEvent.SendFailed, "IOException: broken\npipe", isError: true);

        Assert.Equal(TcpTrafficDirection.Event, e.Direction);
        Assert.Equal(TcpTrafficEvent.SendFailed, e.Event);
        Assert.Equal("SendFailed", e.Kind);
        Assert.Equal("IOException: broken pipe", e.Summary);
        Assert.Equal("IOException: broken\npipe", e.Payload);
        Assert.Equal(0, e.ByteCount);
        Assert.True(e.IsError);

        var plain = log.RecordEvent(TcpTrafficEvent.UserDisconnect);
        Assert.Equal("", plain.Summary);
        Assert.Equal("", plain.Payload);
        Assert.False(plain.IsError);
    }

    [Fact]
    public void Capacity_TrimsOldest_SequenceKeepsIncreasing()
    {
        var log = NewLog(capacity: 3);
        for (var i = 0; i < 5; i++) log.RecordEvent(TcpTrafficEvent.Connecting, i.ToString());

        Assert.Equal(3, log.Count);
        var snapshot = log.Snapshot();
        Assert.Equal(new long[] { 3, 4, 5 }, snapshot.Select(e => e.Sequence));
        Assert.Equal(new[] { "2", "3", "4" }, snapshot.Select(e => e.Summary));

        Assert.Equal(6, log.RecordEvent(TcpTrafficEvent.Connected).Sequence);
        Assert.Equal(new long[] { 4, 5, 6 }, log.Snapshot().Select(e => e.Sequence));
    }

    [Fact]
    public void Snapshot_IsACopy()
    {
        var log = NewLog();
        log.RecordEvent(TcpTrafficEvent.Connecting);
        var snapshot = log.Snapshot();
        log.RecordEvent(TcpTrafficEvent.Connected);
        Assert.Single(snapshot);
        Assert.Equal(2, log.Count);
    }

    // ---- Stats & Clear ----------------------------------------------------------------------------

    [Fact]
    public void Stats_SeparateMessagesPayloadBytesAndErrors()
    {
        var log = NewLog();
        var upload = new TcpMessage.UploadMessage { Id = "u", Filename = "manifest.txt", Size = 100 };
        var header = TcpProtocol.Encode(upload);

        log.RecordEvent(TcpTrafficEvent.Connecting, "10.0.0.5:8080");
        log.RecordEvent(TcpTrafficEvent.Connected, "10.0.0.5:8080");
        log.RecordOutgoing(upload, header);
        log.RecordOutgoingPayload("manifest.txt", new byte[100]);
        log.RecordOutgoing(Stop, StopLine);
        log.RecordIncoming("OK", 3);
        log.RecordIncoming("{\"type\":\"ack\"}", 15);
        log.RecordEvent(TcpTrafficEvent.ReplyTimeout, "no reply", isError: true);
        log.RecordEvent(TcpTrafficEvent.Disconnected, "peer closed", isError: true);

        var expectedSent = (Encoding.UTF8.GetByteCount(header) + 1) + 100 + (StopLine.Length + 1);
        Assert.Equal(new TcpTrafficStats(2, expectedSent, 2, 18, 2), log.Stats);
    }

    [Fact]
    public void Clear_DropsEntriesAndStats_ButNotSequence_AndRaisesCleared()
    {
        var log = NewLog();
        log.RecordOutgoing(Stop, StopLine);
        log.RecordEvent(TcpTrafficEvent.SendFailed, "x", isError: true);
        var cleared = 0;
        log.Cleared += () => cleared++;

        log.Clear();

        Assert.Equal(1, cleared);
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Snapshot());
        Assert.Equal(new TcpTrafficStats(), log.Stats);
        Assert.Equal(3, log.RecordIncoming("OK", 3).Sequence);
        Assert.Equal(new TcpTrafficStats(0, 0, 1, 3, 0), log.Stats);
    }

    [Fact]
    public void Clear_ThrowingSubscriber_DoesNotBreakOthers()
    {
        var log = NewLog();
        var reached = false;
        log.Cleared += () => throw new InvalidOperationException("boom");
        log.Cleared += () => reached = true;

        log.Clear();

        Assert.True(reached);
    }

    // ---- Payload truncation ------------------------------------------------------------------------

    [Fact]
    public void RecordOutgoing_HugeRhythmLine_PreviewIsBoundedAndCheap()
    {
        var log = NewLog();
        var msg = new TcpMessage.RhythmMessage
        {
            Pathology = "ecg42200",
            SampleRate = 500,
            Leads = new Dictionary<Lead, int[]> { [Lead.II] = Enumerable.Repeat(1024, 300_000).ToArray() },
        };
        var line = TcpProtocol.Encode(msg);
        Assert.True(line.Length > 1_000_000);

        log.RecordOutgoing(msg, line); // warm-up (JIT, statics)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = log.RecordOutgoing(msg, line);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A rhythm frame is a bulk kind: its preview gets the small cap, not the 16k general one.
        Assert.Equal(TcpTrafficLog.DefaultMaxBulkPayloadChars, e.Payload.Length);
        Assert.True(e.PayloadTruncated);
        Assert.StartsWith(e.Payload, line, StringComparison.Ordinal);
        Assert.Equal(line.Length + 1, e.ByteCount);
        // A 2k-char preview is ~4 KB; copying the ~1.5 MB line would allocate megabytes.
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void RecordIncoming_LongLine_IsTruncated()
    {
        var log = NewLog(maxPayloadChars: 10);
        var e = log.RecordIncoming("0123456789ABCDEF", 17);
        Assert.Equal("0123456789", e.Payload);
        Assert.True(e.PayloadTruncated);
        Assert.Equal(17, e.ByteCount);
    }

    [Fact]
    public void Preview_ExactFit_IsNotTruncated()
    {
        var log = NewLog(maxPayloadChars: 10);
        var e = log.RecordIncoming("0123456789", 11);
        Assert.Equal("0123456789", e.Payload);
        Assert.False(e.PayloadTruncated);
    }

    [Fact]
    public void Preview_DoesNotSplitSurrogatePair()
    {
        var log = NewLog(maxPayloadChars: 3);
        var e = log.RecordEvent(TcpTrafficEvent.NotSent, "ab\U0001F600cd");
        Assert.Equal("ab", e.Payload);
        Assert.True(e.PayloadTruncated);
    }

    [Fact]
    public void RecordOutgoingPayload_HugeBinary_DecodeIsBounded()
    {
        var log = NewLog();
        var payload = new byte[3 * 1024 * 1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 26);

        log.RecordOutgoingPayload("manifest.txt", payload); // warm-up (JIT, statics)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = log.RecordOutgoingPayload("manifest.txt", payload);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(TcpTrafficDirection.Outgoing, e.Direction);
        Assert.Equal("payload", e.Kind);
        Assert.Equal("payload manifest.txt bytes=3145728", e.Summary);
        Assert.Equal(TcpTrafficLog.DefaultMaxBulkPayloadChars, e.Payload.Length);
        Assert.True(e.PayloadTruncated);
        Assert.Equal(Encoding.ASCII.GetString(payload, 0, e.Payload.Length), e.Payload);
        Assert.Equal(payload.Length, e.ByteCount);
        Assert.Null(e.CorrelationId);
        // A 2k-char preview is ~8 KB (buffer + string); decoding the whole 3 MB would allocate ≥ 6 MB of chars.
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
        Assert.Equal(new TcpTrafficStats(0, 2L * payload.Length, 0, 0, 0), log.Stats);
    }

    [Fact]
    public void RecordOutgoingPayload_SmallUtf8_DecodedWhole()
    {
        var log = NewLog();
        var bytes = Encoding.UTF8.GetBytes("id\tЗаголовок\n42\tСинусовый ритм\n");
        var e = log.RecordOutgoingPayload("manifest.txt", bytes);
        Assert.Equal("id\tЗаголовок\n42\tСинусовый ритм\n", e.Payload);
        Assert.False(e.PayloadTruncated);
        Assert.Equal(bytes.Length, e.ByteCount);
    }

    [Fact]
    public void RecordOutgoingPayload_MultiByteCutAndExactFit()
    {
        var log = NewLog(maxPayloadChars: 5);
        var cut = log.RecordOutgoingPayload("f", Encoding.UTF8.GetBytes("привет мир"));
        Assert.Equal("приве", cut.Payload);
        Assert.True(cut.PayloadTruncated);

        var fit = log.RecordOutgoingPayload("f", Encoding.UTF8.GetBytes("приве"));
        Assert.Equal("приве", fit.Payload);
        Assert.False(fit.PayloadTruncated);

        var empty = log.RecordOutgoingPayload("", ReadOnlySpan<byte>.Empty);
        Assert.Equal("", empty.Payload);
        Assert.False(empty.PayloadTruncated);
        Assert.Equal("payload bytes=0", empty.Summary);
    }

    // ---- Bulk preview cap ---------------------------------------------------------------------------

    [Fact]
    public void BulkCap_DefaultsAndClamping()
    {
        var defaults = new TcpTrafficLog();
        Assert.Equal(TcpTrafficLog.DefaultMaxPayloadChars, defaults.MaxPayloadChars);
        Assert.Equal(TcpTrafficLog.DefaultMaxBulkPayloadChars, defaults.MaxBulkPayloadChars);

        Assert.Equal(100, new TcpTrafficLog(maxPayloadChars: 500, maxBulkPayloadChars: 100).MaxBulkPayloadChars);
        // It only ever tightens the general cap, and never goes below 1.
        Assert.Equal(10, new TcpTrafficLog(maxPayloadChars: 10, maxBulkPayloadChars: 2048).MaxBulkPayloadChars);
        Assert.Equal(10, new TcpTrafficLog(maxPayloadChars: 10).MaxBulkPayloadChars);
        Assert.Equal(1, new TcpTrafficLog(maxBulkPayloadChars: 0).MaxBulkPayloadChars);
    }

    [Fact]
    public void BulkCap_AppliesToRhythmAndPointsFrames_NotToOtherOutgoingFrames()
    {
        var log = NewLog(maxPayloadChars: 50, maxBulkPayloadChars: 20);
        var line = new string('x', 40);

        var rhythm = log.RecordOutgoing(new TcpMessage.RhythmMessage { Pathology = "p1" }, line);
        var points = log.RecordOutgoing(new TcpMessage.PointsMessage { Lead = Lead.II }, line);
        Assert.Equal(new string('x', 20), rhythm.Payload);
        Assert.True(rhythm.PayloadTruncated);
        Assert.Equal(new string('x', 20), points.Payload);
        Assert.True(points.PayloadTruncated);
        // The byte count stays the real wire size.
        Assert.Equal(41, rhythm.ByteCount);

        TcpMessage[] small =
        {
            new TcpMessage.QueryCommand { Pathology = "p1" },
            new TcpMessage.StartCommand(),
            Stop,
            new TcpMessage.UploadMessage { Id = "u1", Filename = "manifest.txt", Size = 1 },
        };
        foreach (var msg in small)
        {
            var e = log.RecordOutgoing(msg, line);
            Assert.Equal(line, e.Payload);
            Assert.False(e.PayloadTruncated);
        }

        // A non-bulk frame is still cut at the general cap.
        var longStart = log.RecordOutgoing(new TcpMessage.StartCommand(), new string('s', 60));
        Assert.Equal(50, longStart.Payload.Length);
        Assert.True(longStart.PayloadTruncated);
    }

    [Fact]
    public void BulkCap_AppliesToUploadPayloadsAndReplyLines_NotToEvents()
    {
        var log = NewLog(maxPayloadChars: 50, maxBulkPayloadChars: 20);
        var text = new string('9', 40);

        var payload = log.RecordOutgoingPayload("manifest.txt", Encoding.ASCII.GetBytes(text));
        Assert.Equal(new string('9', 20), payload.Payload);
        Assert.True(payload.PayloadTruncated);
        Assert.Equal(40, payload.ByteCount);

        var incoming = log.RecordIncoming(text, 41);
        Assert.Equal(new string('9', 20), incoming.Payload);
        Assert.True(incoming.PayloadTruncated);
        Assert.Equal(41, incoming.ByteCount);

        var evt = log.RecordEvent(TcpTrafficEvent.ConnectFailed, text, isError: true);
        Assert.Equal(text, evt.Payload);
        Assert.False(evt.PayloadTruncated);
    }

    // ---- Events ----------------------------------------------------------------------------------

    [Fact]
    public void EntryAdded_RaisedInOrder_ThrowingSubscriberDoesNotBreakRecordingOrOthers()
    {
        var log = NewLog();
        var seen = new List<TcpTrafficEntry>();
        log.EntryAdded += _ => throw new InvalidOperationException("boom");
        log.EntryAdded += seen.Add;

        var a = log.RecordEvent(TcpTrafficEvent.Connecting, "h:1");
        var b = log.RecordOutgoing(Stop, StopLine);
        var c = log.RecordIncoming("no_data", 8);
        var d = log.RecordOutgoingPayload("manifest.txt", new byte[] { 65 });

        Assert.Equal(new[] { a, b, c, d }, seen);
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public void EntryAdded_SubscriberCanReadTheLog()
    {
        var log = NewLog();
        var counts = new List<int>();
        log.EntryAdded += _ => counts.Add(log.Snapshot().Count);

        log.RecordEvent(TcpTrafficEvent.Connecting);
        log.RecordEvent(TcpTrafficEvent.Connected);

        Assert.Equal(new[] { 1, 2 }, counts);
    }

    // ---- FormatBytes ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(812L, "812 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(8294L, "8.1 KB")]
    [InlineData(1048524L, "1023.9 KB")]
    [InlineData(1048575L, "1.00 MB")]
    [InlineData(1048576L, "1.00 MB")]
    [InlineData(1300234L, "1.24 MB")]
    [InlineData(1073741823L, "1.00 GB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(1099511627776L, "1.00 TB")]
    [InlineData(-5L, "-5 B")]
    public void FormatBytes_Boundaries(long bytes, string expected)
    {
        Assert.Equal(expected, TcpTrafficLog.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_IsCultureInvariant()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal("1.5 KB", TcpTrafficLog.FormatBytes(1536));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    // ---- Robustness -------------------------------------------------------------------------------

    [Fact]
    public void NullAndOddInputs_NeverThrow()
    {
        var log = new TcpTrafficLog(capacity: 0, maxPayloadChars: 0, clock: () => throw new InvalidOperationException());
        Assert.Equal(1, log.Capacity);
        Assert.Equal(1, log.MaxPayloadChars);
        Assert.Equal(1, log.MaxBulkPayloadChars);

        var outgoing = log.RecordOutgoing(null!, null!);
        Assert.Equal("unknown", outgoing.Kind);
        Assert.Equal(1, outgoing.ByteCount);

        log.RecordIncoming(null!, -10);
        log.RecordOutgoingPayload(null!, default);
        // A surrogate pair can't fit a 1-char preview buffer.
        log.RecordOutgoingPayload("f", Encoding.UTF8.GetBytes("\U0001F600"));
        log.RecordEvent((TcpTrafficEvent)42, null, isError: true);

        Assert.Equal(1, log.Count);
        Assert.Equal(new TcpTrafficStats(1, 1 + 4, 1, 0, 1), log.Stats);
    }

    // ---- Concurrency ------------------------------------------------------------------------------

    [Fact]
    public void Concurrent_Recording_KeepsStatsCountAndSequencesConsistent()
    {
        const int threads = 8;
        const int perThread = 5000;
        const int total = threads * perThread;
        var log = new TcpTrafficLog(capacity: total);
        var delivered = new List<long>(total);
        log.EntryAdded += e => delivered.Add(e.Sequence); // deliveries are serialized by contract

        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                switch (i % 4)
                {
                    case 0: log.RecordOutgoing(Stop, StopLine); break;
                    case 1: log.RecordIncoming("OK", 3); break;
                    case 2: log.RecordOutgoingPayload("manifest.txt", new byte[10]); break;
                    default: log.RecordEvent(TcpTrafficEvent.SendFailed, "x", isError: true); break;
                }
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        var quarter = total / 4;
        Assert.Equal(new TcpTrafficStats(quarter, quarter * (StopLine.Length + 1L) + quarter * 10L, quarter, quarter * 3L, quarter),
            log.Stats);
        Assert.Equal(total, log.Count);

        var sequences = log.Snapshot().Select(e => e.Sequence).ToList();
        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), sequences);
        Assert.Equal(sequences, delivered);
    }

    [Fact]
    public void Concurrent_Recording_WithSmallCapacity_StaysBounded()
    {
        var log = new TcpTrafficLog(capacity: 100);
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 5000; i++) log.RecordIncoming("no_data", 8);
        });

        Assert.Equal(100, log.Count);
        Assert.Equal(40_000, log.Stats.ReceivedMessages);
        var sequences = log.Snapshot().Select(e => e.Sequence).ToList();
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
        Assert.Equal(40_000, sequences[^1]);
    }
}
