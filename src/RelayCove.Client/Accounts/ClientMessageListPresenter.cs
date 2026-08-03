using System.Globalization;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal static class ClientMessageListPresenter
{
    public static IReadOnlyList<ClientMessageListItemPresentation> Present(
        IEnumerable<MessageDto> messages,
        Guid currentUserId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (currentUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "A current user ID cannot be empty.",
                nameof(currentUserId));
        }

        var items = messages
            .OrderBy(message => message.Id)
            .Select(message => new ClientMessageListItemPresentation(
                message.Id,
                message.SenderId == currentUserId ? "我" : message.SenderDisplayName,
                PresentContent(message),
                message.CreatedAt.ToLocalTime().ToString(
                    "MM-dd HH:mm",
                    CultureInfo.CurrentCulture),
                message.SenderId == currentUserId))
            .ToList();
        return items.AsReadOnly();
    }

    private static string PresentContent(MessageDto message) =>
        message.Type switch
        {
            MessageType.Text => message.Content ?? string.Empty,
            MessageType.Image => "[图片]",
            MessageType.File => "[文件]",
            MessageType.System => message.Content ?? "[系统消息]",
            _ => "[不支持的消息]",
        };
}
