namespace RelayCove.Server.Services;

public sealed record MessageOperationResult<T>(
    MessageOperationStatus Status,
    T? Value = default);
