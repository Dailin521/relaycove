namespace RelayCove.Server.Services;

public sealed record SyncOperationResult(
    SyncOperationStatus Status,
    RelayCove.Shared.Messages.SyncResponse? Value = null);
