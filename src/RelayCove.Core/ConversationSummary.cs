namespace RelayCove.Core;

/// <summary>Compact, cache-backed projection of the newest known message in a conversation.</summary>
public sealed record ConversationSummary
{
    public ConversationSummary(ConversationKey conversation, ChatMessage latestMessage)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(latestMessage);
        if (latestMessage.Conversation != conversation)
        {
            throw new ArgumentException("The latest message must belong to the summarized conversation.", nameof(latestMessage));
        }

        Conversation = conversation;
        LatestMessage = latestMessage;
    }

    public ConversationKey Conversation { get; init; }
    public ChatMessage LatestMessage { get; init; }
}
