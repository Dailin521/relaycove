using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageListItemPresentation(
    long? ServerMessageId,
    Guid ClientMessageId,
    string SenderLabel,
    string Content,
    string Timestamp,
    bool IsOwnMessage,
    MessageSendStatus SendStatus,
    string SendStatusLabel,
    bool CanRetry)
{
    public override string ToString() =>
        $"{nameof(ClientMessageListItemPresentation)} {{ ServerMessageId = [REDACTED], " +
        "ClientMessageId = [REDACTED], " +
        "SenderLabel = [REDACTED], Content = [REDACTED], " +
        $"Timestamp = [REDACTED], IsOwnMessage = {IsOwnMessage}, " +
        $"SendStatus = {SendStatus}, CanRetry = {CanRetry} }}";
}
