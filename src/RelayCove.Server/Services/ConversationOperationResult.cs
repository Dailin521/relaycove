namespace RelayCove.Server.Services;

public sealed record ConversationOperationResult<T>(
    ConversationOperationStatus Status,
    T? Value = default);
