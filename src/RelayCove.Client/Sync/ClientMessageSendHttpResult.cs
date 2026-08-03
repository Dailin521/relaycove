using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientMessageSendHttpResult(
    ClientMessageSendHttpStatus Status,
    MessageDto? Message)
{
    public static ClientMessageSendHttpResult Success(MessageDto message) =>
        new(ClientMessageSendHttpStatus.Success, message);

    public static ClientMessageSendHttpResult Failure(ClientMessageSendHttpStatus status) =>
        new(status, Message: null);

    public override string ToString() =>
        $"{nameof(ClientMessageSendHttpResult)} {{ Status = {Status}, Message = [REDACTED] }}";
}
