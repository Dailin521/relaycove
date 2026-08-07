using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed record ClientMessageListItemPresentation(
    long? ServerMessageId,
    Guid ClientMessageId,
    string SenderLabel,
    string Content,
    string Timestamp,
    string DateSeparatorLabel,
    bool ShowDateSeparator,
    bool ShowNewMessageSeparator,
    bool IsMergedWithPrevious,
    bool IsOwnMessage,
    MessageSendStatus SendStatus,
    string SendStatusLabel,
    bool CanRetry,
    long? ReplyToMessageId,
    string ReplySenderLabel,
    string ReplyContent,
    bool HasReply,
    bool IsReplyTargetAvailable,
    bool CanReply,
    bool CanCopy,
    IReadOnlyList<ClientMessageLinkPresentation> Links,
    bool HasLinks,
    IReadOnlyList<ClientMessageAttachmentPresentation> Attachments,
    bool HasAttachments)
{
    public override string ToString() =>
        $"{nameof(ClientMessageListItemPresentation)} {{ ServerMessageId = [REDACTED], " +
        "ClientMessageId = [REDACTED], " +
        "SenderLabel = [REDACTED], Content = [REDACTED], " +
        "Timestamp = [REDACTED], DateSeparatorLabel = [REDACTED], " +
        $"ShowDateSeparator = {ShowDateSeparator}, " +
        $"ShowNewMessageSeparator = {ShowNewMessageSeparator}, " +
        $"IsMergedWithPrevious = {IsMergedWithPrevious}, " +
        $"IsOwnMessage = {IsOwnMessage}, " +
        $"SendStatus = {SendStatus}, CanRetry = {CanRetry}, " +
        "ReplyToMessageId = [REDACTED], ReplySenderLabel = [REDACTED], " +
        "ReplyContent = [REDACTED], " +
        $"HasReply = {HasReply}, IsReplyTargetAvailable = {IsReplyTargetAvailable}, " +
        $"CanReply = {CanReply}, CanCopy = {CanCopy}, Links = [REDACTED], " +
        $"HasLinks = {HasLinks}, Attachments = [REDACTED], " +
        $"HasAttachments = {HasAttachments} }}";
}
