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
    bool CanRetry,
    long? ReplyToMessageId,
    string ReplySenderLabel,
    string ReplyContent,
    bool HasReply,
    bool IsReplyTargetAvailable,
    bool CanReply)
{
    public override string ToString() =>
        $"{nameof(ClientMessageListItemPresentation)} {{ ServerMessageId = [REDACTED], " +
        "ClientMessageId = [REDACTED], " +
        "SenderLabel = [REDACTED], Content = [REDACTED], " +
        $"Timestamp = [REDACTED], IsOwnMessage = {IsOwnMessage}, " +
        $"SendStatus = {SendStatus}, CanRetry = {CanRetry}, " +
        "ReplyToMessageId = [REDACTED], ReplySenderLabel = [REDACTED], " +
        "ReplyContent = [REDACTED], " +
        $"HasReply = {HasReply}, IsReplyTargetAvailable = {IsReplyTargetAvailable}, " +
        $"CanReply = {CanReply} }}";
}
