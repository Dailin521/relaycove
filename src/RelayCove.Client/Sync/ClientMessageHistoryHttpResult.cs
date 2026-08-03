namespace RelayCove.Client.Sync;

internal sealed record ClientMessageHistoryHttpResult<T>(
    ClientMessageHistoryHttpStatus Status,
    T? Value)
    where T : class
{
    public static ClientMessageHistoryHttpResult<T> Success(T value) =>
        new(ClientMessageHistoryHttpStatus.Success, value);

    public static ClientMessageHistoryHttpResult<T> Failure(
        ClientMessageHistoryHttpStatus status) =>
        new(status, Value: null);

    public override string ToString() =>
        $"{nameof(ClientMessageHistoryHttpResult<T>)} {{ Status = {Status}, " +
        "Value = [REDACTED] }}";
}
