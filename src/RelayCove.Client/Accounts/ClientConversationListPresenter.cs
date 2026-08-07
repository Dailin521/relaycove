using System.Globalization;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal static class ClientConversationListPresenter
{
    private const int MaximumPreviewLength = 96;

    public static IReadOnlyList<ClientConversationListItemPresentation> Present(
        LocalConversationListReadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status == LocalCacheOperationStatus.Ready
            ? outcome.Conversations.Select(Present).ToArray()
            : Array.Empty<ClientConversationListItemPresentation>();
    }

    internal static (
        ClientConversationListItemPresentation? Selection,
        bool ClearPendingSelection) ResolveSelection(
            IReadOnlyList<ClientConversationListItemPresentation> items,
            LocalCacheOperationStatus status,
            Guid? pendingSelectionId,
            Guid? previousSelectionId)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (pendingSelectionId is { } pendingId)
        {
            var pendingSelection = items.FirstOrDefault(item => item.Id == pendingId);
            if (pendingSelection is not null)
            {
                return (pendingSelection, true);
            }

            if (status == LocalCacheOperationStatus.Ready)
            {
                return (Find(items, previousSelectionId), true);
            }
        }

        return (Find(items, previousSelectionId), false);
    }

    private static ClientConversationListItemPresentation Present(
        LocalConversationListItem item)
    {
        var (group, groupTitle, typeIcon, typeLabel) = DescribeType(item.Type);
        return new(
            item.Id,
            group,
            groupTitle,
            typeIcon,
            GetAvatarText(item.Name),
            item.Name,
            typeLabel,
            DescribePreview(item),
            item.LastMessageCreatedAt?.ToLocalTime().ToString(
                "MM-dd HH:mm",
                CultureInfo.CurrentCulture) ?? string.Empty,
            item.UnreadCount > 99
                ? "99+"
                : item.UnreadCount.ToString(CultureInfo.InvariantCulture),
            item.UnreadCount > 0,
            item.IsMuted ? "已静音" : string.Empty);
    }

    private static (ClientConversationGroup Group, string GroupTitle, string TypeIcon,
        string TypeLabel) DescribeType(ConversationType type) =>
        type switch
        {
            ConversationType.PublicChannel => (
                ClientConversationGroup.Public,
                "公开频道",
                "#",
                "频道"),
            ConversationType.PrivateChannel => (
                ClientConversationGroup.Private,
                "私有频道",
                "🔒",
                "私密频道"),
            ConversationType.Direct => (
                ClientConversationGroup.Direct,
                "私聊",
                "@",
                "私聊"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

    private static string GetAvatarText(string name)
    {
        var trimmedName = name.Trim();
        return trimmedName.Length == 0
            ? "?"
            : StringInfo.GetNextTextElement(trimmedName);
    }

    private static string DescribePreview(LocalConversationListItem item)
    {
        if (item.LastMessageId == 0)
        {
            return "暂无消息";
        }

        if (item.LastMessageType is null)
        {
            return "正在同步消息…";
        }

        return item.LastMessageType switch
        {
            MessageType.Image => "[图片]",
            MessageType.File => "[文件]",
            MessageType.System => "[系统消息]",
            _ => NormalizeTextPreview(item.LastMessageContent),
        };
    }

    private static string NormalizeTextPreview(string? content)
    {
        var normalized = string.Join(
            ' ',
            (content ?? string.Empty).Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0)
        {
            return "[空文本消息]";
        }

        return normalized.Length <= MaximumPreviewLength
            ? normalized
            : normalized[..MaximumPreviewLength] + "…";
    }

    private static ClientConversationListItemPresentation? Find(
        IReadOnlyList<ClientConversationListItemPresentation> items,
        Guid? selectionId) =>
        selectionId is { } candidateId
            ? items.FirstOrDefault(item => item.Id == candidateId)
            : null;
}
