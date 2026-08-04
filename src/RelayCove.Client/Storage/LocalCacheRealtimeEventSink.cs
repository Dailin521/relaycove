using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;
using RelayCove.Client.Realtime;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Storage;

public sealed class LocalCacheRealtimeEventSink : IRealtimeEventSink
{
    private readonly AccountScopedLocalCache cache;
    private readonly Func<Guid, CancellationToken, Task> requestConversationReconciliationAsync;
    private readonly Func<Guid?> foregroundConversationIdProvider;
    private readonly Action requestReadThroughUpload;
    private readonly IClientNotificationRoundCoordinator notificationRoundCoordinator;
    private readonly ILogger<LocalCacheRealtimeEventSink> logger;

    public LocalCacheRealtimeEventSink(
        AccountScopedLocalCache cache,
        Func<Guid, CancellationToken, Task> requestConversationReconciliationAsync,
        ILogger<LocalCacheRealtimeEventSink> logger)
        : this(
            cache,
            requestConversationReconciliationAsync,
            foregroundConversationIdProvider: null,
            logger,
            requestReadThroughUpload: null,
            notificationRoundCoordinator: null)
    {
    }

    internal LocalCacheRealtimeEventSink(
        AccountScopedLocalCache cache,
        Func<Guid, CancellationToken, Task> requestConversationReconciliationAsync,
        Func<Guid?>? foregroundConversationIdProvider,
        ILogger<LocalCacheRealtimeEventSink> logger,
        Action? requestReadThroughUpload = null,
        IClientNotificationRoundCoordinator? notificationRoundCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(requestConversationReconciliationAsync);
        ArgumentNullException.ThrowIfNull(logger);
        this.cache = cache;
        this.requestConversationReconciliationAsync = requestConversationReconciliationAsync;
        this.foregroundConversationIdProvider = foregroundConversationIdProvider ??
            (static () => null);
        this.requestReadThroughUpload = requestReadThroughUpload ?? (static () => { });
        this.notificationRoundCoordinator = notificationRoundCoordinator ??
            new NoOpClientNotificationRoundCoordinator();
        this.logger = logger;
    }

    public Task OnConnectionStateChangedAsync(
        ConnectionState state,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task OnNewMessageAsync(
        MessageDto message,
        CancellationToken cancellationToken)
    {
        var context = new LocalMessageIngestionContext(
            IncomingMessageSource.Realtime,
            foregroundConversationIdProvider());
        var outcome = await cache.MergeIncomingMessageAsync(
                message,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == LocalCacheOperationStatus.UnknownConversation)
        {
            logger.LogInformation(
                "A realtime message for an unknown conversation was rejected and reconciliation was requested.");
            await requestConversationReconciliationAsync(message.ConversationId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.Result is not IncomingMessageMergeResult.Conflict &&
            context.IsForegroundConversation(message.ConversationId))
        {
            RequestReadThroughUpload();
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.NotificationCandidateMessageId is { } notificationCandidateMessageId)
        {
            await SubmitNotificationCandidateAsync(
                    notificationCandidateMessageId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (outcome.Status is LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            logger.LogWarning(
                "A realtime message was rejected by the local cache access gate with status {Status}.",
                outcome.Status);
        }
    }

    public async Task OnConversationAccessRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var status = await cache.RevokeConversationAccessAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (status == LocalCacheOperationStatus.FatalScope)
        {
            logger.LogCritical(
                "A realtime access revocation caused the local cache scope to enter fatal fail-closed state.");
        }

        if (status is LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            try
            {
                await notificationRoundCoordinator
                    .ConversationRevokedAsync(conversationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Clearing notification state after realtime revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    public Task OnAccountAccessRevokedAsync(
        AccountAccessRevokedEvent accountAccessRevoked,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task SubmitNotificationCandidateAsync(
        long notificationCandidateMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationRoundCoordinator
                .SubmitRealtimeCandidateAsync(
                    notificationCandidateMessageId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Submitting a realtime notification candidate failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void RequestReadThroughUpload()
    {
        try
        {
            requestReadThroughUpload();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Requesting read-through upload after a foreground realtime merge failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

}
