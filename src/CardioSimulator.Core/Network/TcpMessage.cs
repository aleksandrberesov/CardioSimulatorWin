using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Network;

/// <summary>
/// The line-delimited JSON message hierarchy described in
/// <c>docs/tcp-protocol.md</c>. Construct via object initializers, e.g.
/// <c>new TcpMessage.StopCommand { Id = "m2" }</c>.
/// </summary>
public abstract record TcpMessage
{
    /// <summary>Optional correlation id, echoed back as-is.</summary>
    public string? Id { get; init; }

    /// <summary>The <c>type</c> discriminator written on the wire.</summary>
    public abstract string Type { get; }

    public sealed record StartCommand : TcpMessage
    {
        public const string TypeName = "start";
        public override string Type => TypeName;

        public int? SampleRate { get; init; }
        public IReadOnlyDictionary<string, string> Params { get; init; } = EmptyParams;

        private static readonly IReadOnlyDictionary<string, string> EmptyParams =
            new Dictionary<string, string>();
    }

    public sealed record StopCommand : TcpMessage
    {
        public const string TypeName = "stop";
        public override string Type => TypeName;
    }

    public sealed record PointsMessage : TcpMessage
    {
        public const string TypeName = "points";
        public override string Type => TypeName;

        public Lead? Lead { get; init; }
        public string? Identy { get; init; }
        public int Offset { get; init; }
        public IReadOnlyList<float> Values { get; init; } = Array.Empty<float>();
    }

    public sealed record UploadMessage : TcpMessage
    {
        public const string TypeName = "upload";
        public override string Type => TypeName;

        public required string Filename { get; init; }
        public required long Size { get; init; }
    }

    public sealed record AckMessage : TcpMessage
    {
        public const string TypeName = "ack";
        public override string Type => TypeName;

        public required string Filename { get; init; }
        public required long Bytes { get; init; }
    }
}
