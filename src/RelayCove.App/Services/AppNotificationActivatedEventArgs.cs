namespace RelayCove.App.Services;

public sealed class AppNotificationActivatedEventArgs(string conversationKey) : EventArgs
{
    public string ConversationKey { get; } = string.IsNullOrWhiteSpace(conversationKey)
        ? throw new ArgumentException("A conversation key is required.", nameof(conversationKey))
        : conversationKey;
}
