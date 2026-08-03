namespace RelayCove.Client.Sync;

internal sealed record ClientSyncHttpResult<T>(
    ClientSyncHttpStatus Status,
    T? Value)
    where T : class
{
    public static ClientSyncHttpResult<T> Success(T value) =>
        new(ClientSyncHttpStatus.Success, value);

    public static ClientSyncHttpResult<T> Failure(ClientSyncHttpStatus status) =>
        new(status, null);

    public override string ToString() =>
        $"{nameof(ClientSyncHttpResult<T>)} {{ Status = {Status}, Value = [REDACTED] }}";
}

internal enum ClientSyncHttpStatus
{
    Success = 1,
    AuthenticationRequired = 2,
    TransientFailure = 3,
    ProtocolError = 4,
    CursorInvalid = 5,
    RemoteFailure = 6,
}
