namespace RelayCove.Core;

public sealed record ConnectionState(ConnectionStatus Status, string? Detail = null)
{
    public static ConnectionState SignedOut { get; } = new(ConnectionStatus.SignedOut);
}
