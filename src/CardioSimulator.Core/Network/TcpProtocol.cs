using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Network;

public sealed class TcpProtocolException : Exception
{
    public TcpProtocolException(string message, Exception? cause = null) : base(message, cause) { }
}

/// <summary>
/// Encode / decode for the line-delimited JSON wire format. The encoder is
/// normalizing: optional fields with no value are dropped, <c>points.offset</c>
/// is omitted when 0, and <c>start.params</c> is omitted when empty.
/// </summary>
public static class TcpProtocol
{
    private const string KeyType = "type";
    private const string KeyId = "id";
    private const string KeySampleRate = "sampleRate";
    private const string KeyParams = "params";
    private const string KeyLead = "lead";
    private const string KeyIdenty = "identy";
    private const string KeyOffset = "offset";
    private const string KeyValues = "values";
    private const string KeyFilename = "filename";
    private const string KeySize = "size";
    private const string KeyBytes = "bytes";

    public static string Encode(TcpMessage message) => ToJson(message).ToJsonString();

    public static JsonObject ToJson(TcpMessage message)
    {
        var obj = new JsonObject { [KeyType] = message.Type };
        if (message.Id is not null) obj[KeyId] = message.Id;

        switch (message)
        {
            case TcpMessage.StartCommand start:
                if (start.SampleRate is not null) obj[KeySampleRate] = start.SampleRate.Value;
                if (start.Params.Count > 0)
                {
                    var paramsObj = new JsonObject();
                    foreach (var (k, v) in start.Params) paramsObj[k] = v;
                    obj[KeyParams] = paramsObj;
                }
                break;
            case TcpMessage.StopCommand:
                break;
            case TcpMessage.PointsMessage points:
                if (points.Lead is not null) obj[KeyLead] = points.Lead.Value.ToString();
                if (points.Identy is not null) obj[KeyIdenty] = points.Identy;
                if (points.Offset != 0) obj[KeyOffset] = points.Offset;
                var arr = new JsonArray();
                foreach (var value in points.Values) arr.Add(value);
                obj[KeyValues] = arr;
                break;
            case TcpMessage.UploadMessage upload:
                obj[KeyFilename] = upload.Filename;
                obj[KeySize] = upload.Size;
                break;
            case TcpMessage.AckMessage ack:
                obj[KeyFilename] = ack.Filename;
                obj[KeyBytes] = ack.Bytes;
                break;
        }
        return obj;
    }

    public static TcpMessage Decode(string json)
    {
        JsonObject obj;
        try
        {
            var node = JsonNode.Parse(json);
            obj = node as JsonObject
                ?? throw new TcpProtocolException("Invalid JSON: not a JSON object");
        }
        catch (JsonException e)
        {
            throw new TcpProtocolException($"Invalid JSON: {e.Message}", e);
        }
        return FromJson(obj);
    }

    public static TcpMessage? DecodeOrNull(string json)
    {
        try
        {
            return Decode(json);
        }
        catch (TcpProtocolException)
        {
            return null;
        }
    }

    public static TcpMessage FromJson(JsonObject obj)
    {
        var type = OptString(obj, KeyType)
            ?? throw new TcpProtocolException($"Missing required field: {KeyType}");
        var id = OptString(obj, KeyId);
        return type switch
        {
            TcpMessage.StartCommand.TypeName => new TcpMessage.StartCommand
            {
                Id = id,
                SampleRate = OptInt(obj, KeySampleRate),
                Params = OptStringMap(obj, KeyParams) ?? new Dictionary<string, string>(),
            },
            TcpMessage.StopCommand.TypeName => new TcpMessage.StopCommand { Id = id },
            TcpMessage.PointsMessage.TypeName => new TcpMessage.PointsMessage
            {
                Id = id,
                Lead = OptString(obj, KeyLead) is { } token
                    ? Leads.FromToken(token) ?? throw new TcpProtocolException($"Unknown lead: {token}")
                    : null,
                Identy = OptString(obj, KeyIdenty),
                Offset = OptInt(obj, KeyOffset) ?? 0,
                Values = ParseValues(obj),
            },
            TcpMessage.UploadMessage.TypeName => new TcpMessage.UploadMessage
            {
                Id = id,
                Filename = OptString(obj, KeyFilename)
                    ?? throw new TcpProtocolException($"Missing required field: {KeyFilename}"),
                Size = OptLong(obj, KeySize)
                    ?? throw new TcpProtocolException($"Missing required field: {KeySize}"),
            },
            TcpMessage.AckMessage.TypeName => new TcpMessage.AckMessage
            {
                Id = id,
                Filename = OptString(obj, KeyFilename)
                    ?? throw new TcpProtocolException($"Missing required field: {KeyFilename}"),
                Bytes = OptLong(obj, KeyBytes)
                    ?? throw new TcpProtocolException($"Missing required field: {KeyBytes}"),
            },
            _ => throw new TcpProtocolException($"Unknown message type: {type}"),
        };
    }

    public static IEnumerable<TcpMessage> DecodeFrames(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0).Select(Decode);

    private static IReadOnlyList<float> ParseValues(JsonObject obj)
    {
        if (obj[KeyValues] is not JsonArray arr)
        {
            throw new TcpProtocolException($"Missing required field: {KeyValues}");
        }
        var result = new float[arr.Count];
        for (var i = 0; i < arr.Count; i++)
        {
            result[i] = ToFloat(arr[i], i);
        }
        return result;
    }

    private static float ToFloat(JsonNode? node, int index)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return (float)d;
            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            {
                return (float)ds;
            }
        }
        throw new TcpProtocolException($"Invalid number in {KeyValues} at index {index}");
    }

    private static string? OptString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
            ? s
            : null;

    private static int? OptInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    private static long? OptLong(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    private static IReadOnlyDictionary<string, string>? OptStringMap(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var v) || v is not JsonObject po) return null;
        var map = new Dictionary<string, string>();
        foreach (var (k, node) in po)
        {
            map[k] = node is JsonValue jv && jv.TryGetValue<string>(out var s)
                ? s
                : node?.ToString() ?? string.Empty;
        }
        return map;
    }
}
