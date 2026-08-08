using RelayCove.Client.Presentation;

namespace RelayCove.Client.Accounts;

internal static class ClientConversationFilterPolicy
{
    public static bool Matches(
        ClientConversationListItemPresentation item,
        ClientConversationFilter filter,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(item);
        var matchesFilter = filter switch
        {
            ClientConversationFilter.All => true,
            ClientConversationFilter.Unread => item.HasUnread,
            ClientConversationFilter.Channels => item.Group is
                ClientConversationGroup.Public or ClientConversationGroup.Private,
            ClientConversationFilter.Direct => item.Group == ClientConversationGroup.Direct,
            _ => false,
        };
        if (!matchesFilter)
        {
            return false;
        }

        var keyword = searchText?.Trim();
        return string.IsNullOrEmpty(keyword) ||
            item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Preview.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
