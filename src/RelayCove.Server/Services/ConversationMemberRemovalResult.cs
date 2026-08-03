namespace RelayCove.Server.Services;

internal sealed record ConversationMemberRemovalResult(
    ConversationOperationStatus Status,
    Guid? RemovedUserId = null);
