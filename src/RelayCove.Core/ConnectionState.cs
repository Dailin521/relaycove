namespace RelayCove.Core;

public sealed record ConnectionState(ConnectionStatus Status, string? Detail = null)
{
    public static ConnectionState SignedOut { get; } = new(ConnectionStatus.SignedOut);
    public int RetryAttempt { get; init; }
    public TimeSpan? RetryDelay { get; init; }
    public GatewayErrorCode? FailureCode { get; init; }
    public int? FailureStatusCode { get; init; }
}
