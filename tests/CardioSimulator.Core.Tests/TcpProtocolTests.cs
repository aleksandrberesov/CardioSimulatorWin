using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Network;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TcpProtocolTests
{
    [Fact]
    public void StartCommand_RoundTrips()
    {
        var msg = new TcpMessage.StartCommand
        {
            Id = "m1",
            SampleRate = 250,
            Params = new Dictionary<string, string> { ["source"] = "ecg" },
        };

        var decoded = Assert.IsType<TcpMessage.StartCommand>(TcpProtocol.Decode(TcpProtocol.Encode(msg)));
        Assert.Equal("m1", decoded.Id);
        Assert.Equal(250, decoded.SampleRate);
        Assert.Equal("ecg", decoded.Params["source"]);
    }

    [Fact]
    public void StartCommand_Minimal_OmitsOptionalFields()
    {
        Assert.Equal("{\"type\":\"start\"}", TcpProtocol.Encode(new TcpMessage.StartCommand()));
    }

    [Fact]
    public void StopCommand_RoundTrips()
    {
        var decoded = Assert.IsType<TcpMessage.StopCommand>(
            TcpProtocol.Decode("{\"type\":\"stop\",\"id\":\"m2\"}"));
        Assert.Equal("m2", decoded.Id);
    }

    [Fact]
    public void QueryCommand_RoundTrips()
    {
        var msg = new TcpMessage.QueryCommand
        {
            Id = "q1",
            Pathology = "ecg42200",
            Hash = "abcd1234ef567890",
            Revision = "0",
        };

        var encoded = TcpProtocol.Encode(msg);
        Assert.Contains("\"revision\":\"0\"", encoded);

        var decoded = Assert.IsType<TcpMessage.QueryCommand>(TcpProtocol.Decode(encoded));
        Assert.Equal("q1", decoded.Id);
        Assert.Equal("ecg42200", decoded.Pathology);
        Assert.Equal("abcd1234ef567890", decoded.Hash);
        Assert.Equal("0", decoded.Revision);
    }

    [Fact]
    public void QueryCommand_OmitsRevision_WhenAbsent()
    {
        var encoded = TcpProtocol.Encode(new TcpMessage.QueryCommand { Id = "q2", Pathology = "ecg42200" });

        Assert.DoesNotContain("revision", encoded);
        Assert.Null(Assert.IsType<TcpMessage.QueryCommand>(TcpProtocol.Decode(encoded)).Revision);
    }

    [Fact]
    public void RhythmMessage_RoundTrips_RawIntSamples()
    {
        var msg = new TcpMessage.RhythmMessage
        {
            Id = "r1",
            Pathology = "ecg42200",
            SampleRate = 500,
            Revision = "3af9c1e0",
            Leads = new Dictionary<Lead, int[]>
            {
                [Lead.II] = new[] { 1024, 1000, 1088 },
                [Lead.V3] = new[] { -65, 95, 2284 },
            },
        };

        var encoded = TcpProtocol.Encode(msg);
        Assert.Contains("\"type\":\"rhythm\"", encoded);
        Assert.Contains("\"pathology\":\"ecg42200\"", encoded);
        Assert.Contains("\"revision\":\"3af9c1e0\"", encoded);

        var decoded = Assert.IsType<TcpMessage.RhythmMessage>(TcpProtocol.Decode(encoded));
        Assert.Equal("r1", decoded.Id);
        Assert.Equal(500, decoded.SampleRate);
        Assert.Equal("3af9c1e0", decoded.Revision);
        Assert.Equal(2, decoded.Leads.Count);
        Assert.Equal(new[] { 1024, 1000, 1088 }, decoded.Leads[Lead.II]);
        Assert.Equal(new[] { -65, 95, 2284 }, decoded.Leads[Lead.V3]);
    }

    [Fact]
    public void TimeMessage_RoundTrips_Iso8601WithOffset()
    {
        var msg = new TcpMessage.TimeMessage { Id = "t1", Datetime = "2026-09-17T13:35:12.345+03:00" };

        var encoded = TcpProtocol.Encode(msg);
        Assert.Contains("\"type\":\"time\"", encoded);

        var decoded = Assert.IsType<TcpMessage.TimeMessage>(TcpProtocol.Decode(encoded));
        Assert.Equal("t1", decoded.Id);
        Assert.Equal("2026-09-17T13:35:12.345+03:00", decoded.Datetime);
    }

    [Fact]
    public void TimeMessage_Rejected_WhenDatetimeMissing() =>
        Assert.Throws<TcpProtocolException>(() => TcpProtocol.Decode("{\"type\":\"time\",\"id\":\"t2\"}"));

    [Fact]
    public void PointsMessage_RoundTrips()
    {
        var json = "{\"type\":\"points\",\"id\":\"m3\",\"lead\":\"II\",\"identy\":\"series-1\",\"offset\":10,\"values\":[0.1,0.2,0.3]}";
        var decoded = Assert.IsType<TcpMessage.PointsMessage>(TcpProtocol.Decode(json));

        Assert.Equal("m3", decoded.Id);
        Assert.Equal(Lead.II, decoded.Lead);
        Assert.Equal("series-1", decoded.Identy);
        Assert.Equal(10, decoded.Offset);
        Assert.Equal(3, decoded.Values.Count);
        Assert.Equal(0.1, decoded.Values[0], 5);
        Assert.Equal(0.2, decoded.Values[1], 5);
        Assert.Equal(0.3, decoded.Values[2], 5);
    }

    [Fact]
    public void PointsMessage_OmitsOffsetWhenZero()
    {
        var encoded = TcpProtocol.Encode(new TcpMessage.PointsMessage { Values = new[] { 1f, 2f } });
        Assert.DoesNotContain("offset", encoded);
        Assert.DoesNotContain("lead", encoded);
        Assert.Contains("\"values\":[1,2]", encoded);
    }

    [Fact]
    public void Lead_DecodingIsCaseInsensitiveAndTrimmed()
    {
        var decoded = Assert.IsType<TcpMessage.PointsMessage>(
            TcpProtocol.Decode("{\"type\":\"points\",\"lead\":\" ii \",\"values\":[]}"));
        Assert.Equal(Lead.II, decoded.Lead);
    }

    [Fact]
    public void Upload_RoundTrips()
    {
        var msg = new TcpMessage.UploadMessage { Id = "u1", Filename = "data.zip", Size = 204800 };
        var decoded = Assert.IsType<TcpMessage.UploadMessage>(TcpProtocol.Decode(TcpProtocol.Encode(msg)));
        Assert.Equal("u1", decoded.Id);
        Assert.Equal("data.zip", decoded.Filename);
        Assert.Equal(204800, decoded.Size);
    }

    [Fact]
    public void Ack_RoundTrips()
    {
        var msg = new TcpMessage.AckMessage { Id = "u1", Filename = "data.zip", Bytes = 204800 };
        var decoded = Assert.IsType<TcpMessage.AckMessage>(TcpProtocol.Decode(TcpProtocol.Encode(msg)));
        Assert.Equal("data.zip", decoded.Filename);
        Assert.Equal(204800, decoded.Bytes);
    }

    [Fact]
    public void Encode_IsIdempotentAfterDecode()
    {
        var msg = new TcpMessage.PointsMessage
        {
            Id = "x",
            Lead = Lead.aVR,
            Identy = "s",
            Offset = 7,
            Values = new[] { 0.5f, -1.25f, 2f },
        };
        var encoded = TcpProtocol.Encode(msg);
        var reEncoded = TcpProtocol.Encode(TcpProtocol.Decode(encoded));
        Assert.Equal(encoded, reEncoded);
    }

    [Theory]
    [InlineData("{bad json", "Invalid JSON")]
    [InlineData("{}", "Missing required field")]
    [InlineData("{\"type\":\"foo\"}", "Unknown message type")]
    [InlineData("{\"type\":\"points\"}", "Missing required field")]
    [InlineData("{\"type\":\"points\",\"lead\":\"ZZ\",\"values\":[]}", "Unknown lead")]
    [InlineData("{\"type\":\"points\",\"values\":[\"abc\"]}", "Invalid number in values")]
    [InlineData("{\"type\":\"upload\",\"size\":10}", "Missing required field")]
    public void Decode_RejectsMalformedFrames(string json, string expectedFragment)
    {
        var ex = Assert.Throws<TcpProtocolException>(() => TcpProtocol.Decode(json));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void DecodeOrNull_ReturnsNullForMalformed()
    {
        Assert.Null(TcpProtocol.DecodeOrNull("{bad"));
    }

    [Fact]
    public void DecodeFrames_SkipsBlankLines()
    {
        var lines = new[] { "{\"type\":\"start\"}", "", "   ", "{\"type\":\"stop\"}" };
        var messages = TcpProtocol.DecodeFrames(lines).ToList();
        Assert.Equal(2, messages.Count);
        Assert.IsType<TcpMessage.StartCommand>(messages[0]);
        Assert.IsType<TcpMessage.StopCommand>(messages[1]);
    }
}
