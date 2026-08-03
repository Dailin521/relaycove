namespace RelayCove.Server.Hubs;

internal static class ConversationHubGroup
{
    public static string For(Guid conversationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(conversationId, Guid.Empty);
        return $"conversation:{conversationId:D}";
    }
}
