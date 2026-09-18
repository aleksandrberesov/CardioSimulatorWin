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

    /// <summary>Optional user/session identifier.</summary>
    public string? Uid { get; init; }

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

        /// <summary>
        /// Content revision of this rhythm on the sending install: <c>"0"</c> for the rhythm exactly as the
        /// dataset shipped it, otherwise a short fingerprint of the instructor's edited copy. Carried
        /// identically by the <see cref="RhythmMessage"/> that answers a <c>no_data</c>, so the server can key
        /// its cache by (pathology, revision).
        /// </summary>
        public string? Revision { get; init; }
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

        /// <summary>The same revision the <see cref="QueryCommand"/> carried — see
        /// <see cref="QueryCommand.Revision"/>.</summary>
        public string? Revision { get; init; }

        public IReadOnlyDictionary<Lead, int[]> Leads { get; init; } = EmptyLeads;

        private static readonly IReadOnlyDictionary<Lead, int[]> EmptyLeads =
            new Dictionary<Lead, int[]>();
    }

    /// <summary>
    /// The client's system date/time, pushed once per connection (right after connecting, before the catalog)
    /// so the server can show or log the wall-clock time the app is running on. Advisory: the server must not
    /// reply to it — an extra reply would be misread as a cache verdict for the first <see cref="QueryCommand"/>.
    /// </summary>
    public sealed record TimeMessage : TcpMessage
    {
        public const string TypeName = "time";
        public override string Type => TypeName;

        /// <summary>Local system time as ISO-8601 with the UTC offset, e.g. <c>2026-09-17T13:35:12.345+03:00</c>.</summary>
        public required string Datetime { get; init; }
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

        public string? Filename { get; init; }
        public string? Status { get; init; }
        public long? Size { get; init; }
        public long? Bytes { get; init; }
    }
}
