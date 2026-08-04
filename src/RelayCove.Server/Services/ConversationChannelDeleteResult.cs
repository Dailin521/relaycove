namespace RelayCove.Server.Services;

public sealed record ConversationChannelDeleteResult(
    ConversationOperationStatus Status,
    IReadOnlyList<Guid>? RevokedUserIds = null);
