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

    /// <summary>
    /// Cache probe sent when the user selects a rhythm: "do you already have this rhythm's data?".
    /// The server answers <c>OK</c> (cached — the app sends nothing) or <c>no_data</c> (the app then sends
    /// a <see cref="RhythmMessage"/>). Carries the pathology id and a content <see cref="Hash"/> so the
    /// server can key its cache by (pathology, hash) and miss when an edit changes the samples.
    /// </summary>
    public sealed record QueryCommand : TcpMessage
    {
        public const string TypeName = "query";
        public override string Type => TypeName;

        public required string Pathology { get; init; }
        public string? Hash { get; init; }
    }

    /// <summary>
    /// The whole selected rhythm in one message: every stored lead's <b>raw</b> <c>.dat</c> samples
    /// (ADC integers, baseline-centered on 1024 — not baseline-zeroed), keyed by lead token. Sent once per
    /// selection when the server replied <c>no_data</c> to the <see cref="QueryCommand"/>. Not a stream.
    /// </summary>
    public sealed record RhythmMessage : TcpMessage
    {
        public const string TypeName = "rhythm";
        public override string Type => TypeName;

        public required string Pathology { get; init; }
        public int? SampleRate { get; init; }
        public IReadOnlyDictionary<Lead, int[]> Leads { get; init; } = EmptyLeads;

        private static readonly IReadOnlyDictionary<Lead, int[]> EmptyLeads =
            new Dictionary<Lead, int[]>();
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
