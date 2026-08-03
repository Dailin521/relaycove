using System.Globalization;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal static class ClientMessageListPresenter
{
    public static IReadOnlyList<ClientMessageListItemPresentation> Present(
        IEnumerable<MessageDto> messages,
        Guid currentUserId) =>
        Present(messages, Array.Empty<LocalPendingMessage>(), currentUserId);

    public static IReadOnlyList<ClientMessageListItemPresentation> Present(
        IEnumerable<MessageDto> messages,
        IEnumerable<LocalPendingMessage> pendingMessages,
        Guid currentUserId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(pendingMessages);
        if (currentUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "A current user ID cannot be empty.",
                nameof(currentUserId));
        }

        var items = messages
            .OrderBy(message => message.Id)
            .Select(message => new ClientMessageListItemPresentation(
                ServerMessageId: message.Id,
                message.ClientMessageId,
                message.SenderId == currentUserId ? "我" : message.SenderDisplayName,
                PresentContent(message),
                message.CreatedAt.ToLocalTime().ToString(
                    "MM-dd HH:mm",
                    CultureInfo.CurrentCulture),
                message.SenderId == currentUserId,
                MessageSendStatus.Sent,
                SendStatusLabel: string.Empty,
                CanRetry: false))
            .ToList();
        items.AddRange(pendingMessages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.LocalId)
            .Select(message => new ClientMessageListItemPresentation(
                ServerMessageId: null,
                message.ClientMessageId,
                message.SenderId == currentUserId ? "我" : message.SenderDisplayName,
                PresentContent(message.Type, message.Content),
                message.CreatedAt.ToLocalTime().ToString(
                    "MM-dd HH:mm",
                    CultureInfo.CurrentCulture),
                message.SenderId == currentUserId,
                message.SendStatus,
                message.SendStatus == MessageSendStatus.Failed ? "发送失败" : "发送中…",
                CanRetry: message.SendStatus == MessageSendStatus.Failed)));
        return items.AsReadOnly();
    }

    private static string PresentContent(MessageDto message) =>
        PresentContent(message.Type, message.Content);

    private static string PresentContent(MessageType type, string? content) =>
        type switch
        {
            MessageType.Text => content ?? string.Empty,
            MessageType.Image => "[图片]",
            MessageType.File => "[文件]",
            MessageType.System => content ?? "[系统消息]",
            _ => "[不支持的消息]",
        };
}
