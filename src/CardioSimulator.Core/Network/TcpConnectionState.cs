namespace CardioSimulator.Core.Network;

/// <summary>Connection lifecycle states for the TCP link.</summary>
public abstract record TcpConnectionState
{
    public sealed record Disconnected : TcpConnectionState;

    public sealed record Connecting : TcpConnectionState;

    public sealed record Connected : TcpConnectionState;

    public sealed record Error(string Message) : TcpConnectionState;
}
