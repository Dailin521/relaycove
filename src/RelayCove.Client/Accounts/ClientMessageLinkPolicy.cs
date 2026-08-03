namespace RelayCove.Client.Accounts;

internal static class ClientMessageLinkPolicy
{
    public static bool IsCurrent(
        ClientMessageListSnapshot snapshot,
        ClientMessageLinkPresentation requestedLink)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requestedLink);
        return snapshot.Status == ClientMessageListStatus.Ready &&
            snapshot.ConversationId.HasValue &&
            snapshot.Messages.Any(message => message.Links.Contains(requestedLink));
    }
}
