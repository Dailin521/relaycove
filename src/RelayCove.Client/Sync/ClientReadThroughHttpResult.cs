using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientReadThroughHttpResult(
    ClientReadThroughHttpStatus Status,
    ConversationReadReceipt? Receipt)
{
    public static ClientReadThroughHttpResult Success(ConversationReadReceipt receipt) =>
        new(ClientReadThroughHttpStatus.Success, receipt);

    public static ClientReadThroughHttpResult Failure(ClientReadThroughHttpStatus status) =>
        new(status, null);

    public override string ToString() =>
        $"{nameof(ClientReadThroughHttpResult)} {{ Status = {Status}, Receipt = [REDACTED] }}";
}

internal enum ClientReadThroughHttpStatus
{
    Success = 1,
    AuthenticationRequired = 2,
    TransientFailure = 3,
    ProtocolError = 4,
    AccessDenied = 5,
    RemoteFailure = 6,
    AccessRevoked = 7,
}
