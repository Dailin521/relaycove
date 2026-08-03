namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationDispatchOutcome(
    ClientNotificationDispatchStatus Status,
    int CandidateCount,
    int AcceptedCount,
    int HandledWithoutPlatformCount)
{
    public override string ToString() =>
        $"{nameof(ClientNotificationDispatchOutcome)} {{ Status = {Status}, " +
        $"CandidateCount = {CandidateCount}, AcceptedCount = {AcceptedCount}, " +
        $"HandledWithoutPlatformCount = {HandledWithoutPlatformCount} }}";
}
