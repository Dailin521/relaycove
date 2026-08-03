namespace RelayCove.Client.Sync;

internal sealed record ClientReadThroughRunOutcome(
    ClientReadThroughRunStatus Status,
    int RequestCount,
    int ReceiptCount)
{
    public override string ToString() =>
        $"{nameof(ClientReadThroughRunOutcome)} {{ Status = {Status}, " +
        $"RequestCount = {RequestCount}, ReceiptCount = {ReceiptCount} }}";
}
