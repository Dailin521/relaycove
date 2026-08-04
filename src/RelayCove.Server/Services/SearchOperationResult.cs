namespace RelayCove.Server.Services;

public sealed record SearchOperationResult<T>(
    SearchOperationStatus Status,
    T? Value = default);
