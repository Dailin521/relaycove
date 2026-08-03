namespace RelayCove.Client.Sync;

internal sealed record ClientMessageSendOutcome(
    ClientMessageSendStatus Status,
    bool PendingCommitted)
{
    public static ClientMessageSendOutcome Failure(
        ClientMessageSendStatus status,
        bool pendingCommitted = false) =>
        new(status, pendingCommitted);

    public override string ToString() =>
        $"{nameof(ClientMessageSendOutcome)} {{ Status = {Status}, " +
        $"PendingCommitted = {PendingCommitted} }}";
}
