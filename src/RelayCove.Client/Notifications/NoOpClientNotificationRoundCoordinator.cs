using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed class NoOpClientNotificationRoundCoordinator :
    IClientNotificationRoundCoordinator
{
    private long generation;

    public ClientNotificationRoundToken OpenRound(SyncReason reason) =>
        new(Interlocked.Increment(ref generation), reason);

    public Task SnapshotCommittedAsync(
        ClientNotificationRoundToken token,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public void SubmitSyncCandidates(
        ClientNotificationRoundToken token,
        IReadOnlyCollection<long> messageIds)
    {
    }

    public Task SubmitRealtimeCandidateAsync(
        long messageId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseRoundAsync(
        ClientNotificationRoundToken token,
        ClientSyncRunStatus status) => Task.CompletedTask;

    public Task ConversationRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
