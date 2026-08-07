using System.Collections.Frozen;
using System.Globalization;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal static class ClientMessageListPresenter
{
    private static readonly TimeSpan MaximumMergeInterval = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<ClientMessageListItemPresentation> Present(
        IEnumerable<MessageDto> messages,
        Guid currentUserId) =>
        Present(
            messages,
            Array.Empty<LocalPendingMessage>(),
            currentUserId,
            newMessageSeparatorBeforeMessageId: null,
            downloadedAttachmentIds: null);

    public static IReadOnlyList<ClientMessageListItemPresentation> Present(
        IEnumerable<MessageDto> messages,
        IEnumerable<LocalPendingMessage> pendingMessages,
        Guid currentUserId,
        long? newMessageSeparatorBeforeMessageId = null,
        IReadOnlySet<Guid>? downloadedAttachmentIds = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(pendingMessages);
        if (currentUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "A current user ID cannot be empty.",
                nameof(currentUserId));
        }
        if (newMessageSeparatorBeforeMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newMessageSeparatorBeforeMessageId));
        }

        var confirmedMessages = messages
            .OrderBy(message => message.Id)
            .ToArray();
        var downloadedIds = downloadedAttachmentIds ?? FrozenSet<Guid>.Empty;
        var messagesById = confirmedMessages.ToDictionary(message => message.Id);
        var items = new List<ClientMessageListItemPresentation>();
        DateTime? previousLocalDate = null;
        DateTimeOffset? previousCreatedAt = null;
        Guid? previousSenderId = null;
        foreach (var message in confirmedMessages)
        {
            var content = PresentContent(message);
            var links = ClientMessageLinkParser.Parse(content);
            var attachments = PresentAttachments(message, downloadedIds);
            AppendWithDateSeparator(
                items,
                ref previousLocalDate,
                ref previousCreatedAt,
                ref previousSenderId,
                message.CreatedAt,
                message.SenderId,
                new ClientMessageListItemPresentation(
                ServerMessageId: message.Id,
                message.ClientMessageId,
                message.SenderId == currentUserId ? "我" : message.SenderDisplayName,
                content,
                message.CreatedAt.ToLocalTime().ToString(
                    "MM-dd HH:mm",
                    CultureInfo.CurrentCulture),
                DateSeparatorLabel: string.Empty,
                ShowDateSeparator: false,
                ShowNewMessageSeparator:
                    newMessageSeparatorBeforeMessageId == message.Id &&
                    message.SenderId != currentUserId,
                IsMergedWithPrevious: false,
                message.SenderId == currentUserId,
                MessageSendStatus.Sent,
                SendStatusLabel: string.Empty,
                CanRetry: false,
                message.ReplyToMessageId,
                ReplySenderLabel(message.ReplyToMessageId, messagesById, currentUserId),
                ReplyContent(message.ReplyToMessageId, messagesById),
                HasReply: message.ReplyToMessageId is > 0,
                IsReplyTargetAvailable: ReplyTargetIsAvailable(
                    message.ReplyToMessageId,
                    messagesById),
                CanReply: true,
                CanCopy: !string.IsNullOrEmpty(content),
                links,
                HasLinks: links.Count != 0,
                attachments,
                HasAttachments: attachments.Count != 0));
        }

        foreach (var message in pendingMessages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.LocalId))
        {
            var content = PresentContent(message.Type, message.Content);
            var links = ClientMessageLinkParser.Parse(content);
            AppendWithDateSeparator(
                items,
                ref previousLocalDate,
                ref previousCreatedAt,
                ref previousSenderId,
                message.CreatedAt,
                message.SenderId,
                new ClientMessageListItemPresentation(
                ServerMessageId: null,
                message.ClientMessageId,
                message.SenderId == currentUserId ? "我" : message.SenderDisplayName,
                content,
                message.CreatedAt.ToLocalTime().ToString(
                    "MM-dd HH:mm",
                    CultureInfo.CurrentCulture),
                DateSeparatorLabel: string.Empty,
                ShowDateSeparator: false,
                ShowNewMessageSeparator: false,
                IsMergedWithPrevious: false,
                message.SenderId == currentUserId,
                message.SendStatus,
                message.SendStatus == MessageSendStatus.Failed ? "发送失败" : "发送中…",
                CanRetry: message.SendStatus == MessageSendStatus.Failed,
                message.ReplyToMessageId,
                ReplySenderLabel(message.ReplyToMessageId, messagesById, currentUserId),
                ReplyContent(message.ReplyToMessageId, messagesById),
                HasReply: message.ReplyToMessageId is > 0,
                IsReplyTargetAvailable: ReplyTargetIsAvailable(
                    message.ReplyToMessageId,
                    messagesById),
                CanReply: false,
                CanCopy: !string.IsNullOrEmpty(content),
                links,
                HasLinks: links.Count != 0,
                Attachments: Array.Empty<ClientMessageAttachmentPresentation>(),
                HasAttachments: false));
        }

        return items.AsReadOnly();
    }

    private static void AppendWithDateSeparator(
        ICollection<ClientMessageListItemPresentation> items,
        ref DateTime? previousLocalDate,
        ref DateTimeOffset? previousCreatedAt,
        ref Guid? previousSenderId,
        DateTimeOffset createdAt,
        Guid senderId,
        ClientMessageListItemPresentation item)
    {
        var localCreatedAt = createdAt.ToLocalTime();
        var localDate = localCreatedAt.Date;
        var showDateSeparator = previousLocalDate != localDate;
        var isMergedWithPrevious =
            previousSenderId == senderId &&
            previousCreatedAt is { } previousCreated &&
            createdAt >= previousCreated &&
            createdAt - previousCreated <= MaximumMergeInterval &&
            !showDateSeparator &&
            !item.ShowNewMessageSeparator;
        items.Add(item with
        {
            DateSeparatorLabel = localCreatedAt.ToString(
                "yyyy-MM-dd",
                CultureInfo.CurrentCulture),
            ShowDateSeparator = showDateSeparator,
            IsMergedWithPrevious = isMergedWithPrevious,
        });
        previousLocalDate = localDate;
        previousCreatedAt = createdAt;
        previousSenderId = senderId;
    }

    private static string ReplySenderLabel(
        long? replyToMessageId,
        IReadOnlyDictionary<long, MessageDto> messagesById,
        Guid currentUserId) =>
        replyToMessageId is { } messageId &&
        messagesById.TryGetValue(messageId, out var target)
            ? target.SenderId == currentUserId
                ? "回复 我"
                : $"回复 {target.SenderDisplayName}"
            : replyToMessageId is > 0
                ? "回复消息"
                : string.Empty;

    private static string ReplyContent(
        long? replyToMessageId,
        IReadOnlyDictionary<long, MessageDto> messagesById) =>
        replyToMessageId is { } messageId &&
        messagesById.TryGetValue(messageId, out var target)
            ? PresentContent(target)
            : replyToMessageId is > 0
                ? "原消息未加载，点击定位"
                : string.Empty;

    private static bool ReplyTargetIsAvailable(
        long? replyToMessageId,
        IReadOnlyDictionary<long, MessageDto> messagesById) =>
        replyToMessageId is { } messageId && messagesById.ContainsKey(messageId);

    private static string PresentContent(MessageDto message) =>
        PresentContent(message.Type, message.Content);

    private static IReadOnlyList<ClientMessageAttachmentPresentation> PresentAttachments(
        MessageDto message,
        IReadOnlySet<Guid> downloadedAttachmentIds)
    {
        if (!ClientAttachmentMetadataPolicy.IsValidCollection(message.Type, message.Attachments))
        {
            return Array.Empty<ClientMessageAttachmentPresentation>();
        }

        return message.Attachments
            .Select(attachment => new ClientMessageAttachmentPresentation(
                message.ClientMessageId,
                attachment.Id,
                attachment.OriginalFileName,
                FormatDisplaySize(attachment.Size),
                attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                downloadedAttachmentIds.Contains(attachment.Id)))
            .ToArray();
    }

    private static string FormatDisplaySize(long size) =>
        size switch
        {
            < 1024 => $"{size.ToString(CultureInfo.InvariantCulture)} B",
            < 1024 * 1024 =>
                $"{(size / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KiB",
            _ => $"{(size / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MiB",
        };

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
