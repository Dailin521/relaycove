namespace RelayCove.App.Services;

public sealed record AppMessageNotification(
    string ConversationKey,
    string Title,
    string Body,
    string? SenderAvatarUrl = null);
