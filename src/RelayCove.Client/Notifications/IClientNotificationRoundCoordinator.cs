using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal interface IClientNotificationRoundCoordinator : IAsyncDisposable
{
    ClientNotificationRoundToken OpenRound(SyncReason reason);

    Task SnapshotCommittedAsync(
        ClientNotificationRoundToken token,
        CancellationToken cancellationToken);

    void SubmitSyncCandidates(
        ClientNotificationRoundToken token,
        IReadOnlyCollection<long> messageIds);

    Task SubmitRealtimeCandidateAsync(
        long messageId,
        CancellationToken cancellationToken);

    Task CloseRoundAsync(
        ClientNotificationRoundToken token,
        ClientSyncRunStatus status);

    Task ConversationRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}
