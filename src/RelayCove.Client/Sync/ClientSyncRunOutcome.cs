using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

public sealed record ClientSyncRunOutcome(
    ClientSyncRunStatus Status,
    SyncReason Reason,
    int RoundsExecuted)
{
    public override string ToString() =>
        $"{nameof(ClientSyncRunOutcome)} {{ Status = {Status}, Reason = {Reason}, " +
        $"RoundsExecuted = {RoundsExecuted} }}";
}
