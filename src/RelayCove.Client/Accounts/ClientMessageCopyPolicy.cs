namespace RelayCove.Client.Accounts;

internal static class ClientMessageCopyPolicy
{
    public static bool TryResolveContent(
        ClientMessageListSnapshot snapshot,
        ClientMessageListItemPresentation requestedItem,
        out string content)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requestedItem);
        content = string.Empty;
        if (snapshot.Status != ClientMessageListStatus.Ready ||
            !snapshot.ConversationId.HasValue ||
            !requestedItem.CanCopy)
        {
            return false;
        }

        var currentItem = snapshot.Messages.FirstOrDefault(item => item == requestedItem);
        if (currentItem is null ||
            !currentItem.CanCopy ||
            string.IsNullOrEmpty(currentItem.Content))
        {
            return false;
        }

        content = currentItem.Content;
        return true;
    }
}
