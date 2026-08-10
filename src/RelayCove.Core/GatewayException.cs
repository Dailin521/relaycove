namespace RelayCove.Core;

public sealed class GatewayException : Exception
{
    public GatewayException(
        GatewayErrorKind kind,
        GatewayErrorCode code,
        int? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base($"Zulip request failed ({code}).")
    {
        Kind = kind;
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        CauseTypeName = innerException?.GetType().FullName;
    }

    public GatewayErrorKind Kind { get; }
    public GatewayErrorCode Code { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public string? CauseTypeName { get; }
}
