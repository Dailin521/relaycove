namespace RelayCove.Core;

/// <summary>Server permissions for the current account; omitted time limits default to ten minutes.</summary>
public sealed record MessageActionPolicy
{
    public const int FallbackLimitSeconds = 600;
    public static MessageActionPolicy Unavailable { get; } = new() { IsAvailable = false, AllowEditing = false, CanDeleteOwn = false };

    public bool IsAvailable { get; init; } = true;
    public bool AllowEditing { get; init; } = true;
    public int? EditLimitSeconds { get; init; } = FallbackLimitSeconds;
    public int? DeleteLimitSeconds { get; init; } = FallbackLimitSeconds;
    public bool CanDeleteOwn { get; init; } = true;
    public bool CanDeleteAny { get; init; }
    public IReadOnlyDictionary<long, MessageActionChannelPolicy> Channels { get; init; } =
        new Dictionary<long, MessageActionChannelPolicy>();

    public bool CanEdit(ChatMessage message, long? currentUserId, DateTimeOffset now) =>
        IsAvailable && message.SenderId == currentUserId && AllowEditing && !IsArchived(message) &&
        WithinLimit(message, now, EditLimitSeconds);

    public bool CanDelete(ChatMessage message, long? currentUserId, DateTimeOffset now)
    {
        // RelayCove currently exposes mutations only for the account's own messages.
        if (!IsAvailable || message.SenderId != currentUserId || IsArchived(message)) return false;
        var channel = message.Conversation is ChannelTopic topic ? Channels.GetValueOrDefault(topic.ChannelId) : null;
        if (CanDeleteAny || channel?.CanDeleteAny == true) return true;
        return (CanDeleteOwn || channel?.CanDeleteOwn == true) && WithinLimit(message, now, DeleteLimitSeconds);
    }

    private bool IsArchived(ChatMessage message) => message.Conversation is ChannelTopic topic &&
        Channels.GetValueOrDefault(topic.ChannelId)?.IsArchived == true;

    private static bool WithinLimit(ChatMessage message, DateTimeOffset now, int? seconds) =>
        seconds is null || seconds > 0 && (now - message.Timestamp).TotalSeconds < seconds;
}
