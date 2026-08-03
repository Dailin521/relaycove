using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientMessageHistoryCoordinator : IAsyncDisposable
{
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientMessageHistoryHttpTransport transport;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientMessageHistoryCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int disposed;

    public ClientMessageHistoryCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientMessageHistoryCoordinator> logger,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.conversationRevokedAsync = conversationRevokedAsync ??
            (static (_, _) => Task.CompletedTask);
        if (!string.Equals(identity.Id, localCache.Identity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The local cache must belong to the message history account scope.",
                nameof(localCache));
        }

        transport = new ClientMessageHistoryHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger);
    }

    public async Task<ClientMessageHistoryPageOutcome> LoadHistoryAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        try
        {
            var httpResult = await transport
                .GetHistoryAsync(
                    conversationId,
                    beforeMessageId,
                    limit,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (httpResult.Status != ClientMessageHistoryHttpStatus.Success)
            {
                return await HandleHistoryHttpFailureAsync(
                        conversationId,
                        httpResult.Status)
                    .ConfigureAwait(false);
            }

            var response = httpResult.Value!;
            if (!TryValidateHistoryResponse(
                    response,
                    conversationId,
                    beforeMessageId,
                    limit))
            {
                logger.LogWarning("A message History response failed protocol validation.");
                return ClientMessageHistoryPageOutcome.Failure(
                    ClientMessageLoadStatus.ProtocolError);
            }

            var commit = await localCache
                .ApplyHistoryPageAsync(
                    conversationId,
                    response.Messages,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (commit.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientMessageHistoryPageOutcome.Failure(
                    MapLocalStatus(commit.Status));
            }

            return new ClientMessageHistoryPageOutcome(
                ClientMessageLoadStatus.Completed,
                response.Messages.ToList().AsReadOnly(),
                response.NextBeforeMessageId,
                response.HasMore);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageHistoryPageOutcome.Failure(
                ClientMessageLoadStatus.Canceled);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return ClientMessageHistoryPageOutcome.Failure(
                ClientMessageLoadStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Loading a message History page failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageHistoryPageOutcome.Failure(
                ClientMessageLoadStatus.LocalCacheFailure);
        }
    }

    public async Task<ClientMessageAroundOutcome> LoadAroundAsync(
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        try
        {
            var httpResult = await transport
                .GetAroundAsync(
                    conversationId,
                    messageId,
                    before,
                    after,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (httpResult.Status != ClientMessageHistoryHttpStatus.Success)
            {
                return await HandleAroundHttpFailureAsync(
                        conversationId,
                        httpResult.Status)
                    .ConfigureAwait(false);
            }

            var response = httpResult.Value!;
            if (!TryValidateAroundResponse(
                    response,
                    conversationId,
                    messageId,
                    before,
                    after))
            {
                logger.LogWarning("A message Around response failed protocol validation.");
                return ClientMessageAroundOutcome.Failure(
                    ClientMessageLoadStatus.ProtocolError);
            }

            var commit = await localCache
                .ApplyHistoryPageAsync(
                    conversationId,
                    response.Messages,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (commit.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientMessageAroundOutcome.Failure(
                    MapLocalStatus(commit.Status));
            }

            return new ClientMessageAroundOutcome(
                ClientMessageLoadStatus.Completed,
                response.Messages.ToList().AsReadOnly(),
                response.TargetMessageId,
                response.HasMoreBefore,
                response.HasMoreAfter);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageAroundOutcome.Failure(ClientMessageLoadStatus.Canceled);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return ClientMessageAroundOutcome.Failure(ClientMessageLoadStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Loading a message Around window failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageAroundOutcome.Failure(
                ClientMessageLoadStatus.LocalCacheFailure);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetimeCancellation.Cancel();
            lifetimeCancellation.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<ClientMessageHistoryPageOutcome> HandleHistoryHttpFailureAsync(
        Guid conversationId,
        ClientMessageHistoryHttpStatus status)
    {
        if (status != ClientMessageHistoryHttpStatus.AccessRevoked)
        {
            return ClientMessageHistoryPageOutcome.Failure(MapHttpStatus(status));
        }

        var revokeStatus = await RevokeConversationAsync(conversationId).ConfigureAwait(false);
        return ClientMessageHistoryPageOutcome.Failure(revokeStatus);
    }

    private async Task<ClientMessageAroundOutcome> HandleAroundHttpFailureAsync(
        Guid conversationId,
        ClientMessageHistoryHttpStatus status)
    {
        if (status != ClientMessageHistoryHttpStatus.AccessRevoked)
        {
            return ClientMessageAroundOutcome.Failure(MapHttpStatus(status));
        }

        var revokeStatus = await RevokeConversationAsync(conversationId).ConfigureAwait(false);
        return ClientMessageAroundOutcome.Failure(revokeStatus);
    }

    private async Task<ClientMessageLoadStatus> RevokeConversationAsync(Guid conversationId)
    {
        var revokeStatus = await localCache
            .RevokeConversationAccessAsync(conversationId, CancellationToken.None)
            .ConfigureAwait(false);
        if (revokeStatus is LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            try
            {
                await conversationRevokedAsync(conversationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Clearing notification state after History revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        return revokeStatus == LocalCacheOperationStatus.RevokedConversation
            ? ClientMessageLoadStatus.AccessRevoked
            : ClientMessageLoadStatus.LocalCacheFailure;
    }

    private static bool TryValidateHistoryResponse(
        MessageHistoryResponse? response,
        Guid conversationId,
        long? beforeMessageId,
        int limit)
    {
        if (response?.Messages is null ||
            response.Messages.Count > limit ||
            response.HasMore != response.NextBeforeMessageId.HasValue ||
            (response.HasMore && response.Messages.Count == 0) ||
            (response.HasMore && response.Messages.Count != limit) ||
            (response.HasMore &&
             response.NextBeforeMessageId != response.Messages[0].Id))
        {
            return false;
        }

        long previousId = 0;
        foreach (var message in response.Messages)
        {
            if (message is null ||
                message.ConversationId != conversationId ||
                message.Id <= previousId ||
                (beforeMessageId.HasValue && message.Id >= beforeMessageId.Value))
            {
                return false;
            }

            previousId = message.Id;
        }

        return true;
    }

    private static bool TryValidateAroundResponse(
        MessageAroundResponse? response,
        Guid conversationId,
        long targetMessageId,
        int before,
        int after)
    {
        if (response?.Messages is null ||
            response.TargetMessageId != targetMessageId ||
            response.Messages.Count > before + after + 1)
        {
            return false;
        }

        var targetCount = 0;
        var beforeCount = 0;
        var afterCount = 0;
        long previousId = 0;
        foreach (var message in response.Messages)
        {
            if (message is null ||
                message.ConversationId != conversationId ||
                message.Id <= previousId)
            {
                return false;
            }

            if (message.Id < targetMessageId)
            {
                beforeCount++;
            }
            else if (message.Id > targetMessageId)
            {
                afterCount++;
            }
            else
            {
                targetCount++;
            }

            previousId = message.Id;
        }

        return targetCount == 1 &&
            beforeCount <= before &&
            afterCount <= after &&
            (!response.HasMoreBefore || beforeCount == before) &&
            (!response.HasMoreAfter || afterCount == after);
    }

    private static ClientMessageLoadStatus MapHttpStatus(
        ClientMessageHistoryHttpStatus status) =>
        status switch
        {
            ClientMessageHistoryHttpStatus.AuthenticationRequired =>
                ClientMessageLoadStatus.AuthenticationRequired,
            ClientMessageHistoryHttpStatus.AccessDenied =>
                ClientMessageLoadStatus.AccessDenied,
            ClientMessageHistoryHttpStatus.TransientFailure =>
                ClientMessageLoadStatus.TransientFailure,
            ClientMessageHistoryHttpStatus.ProtocolError =>
                ClientMessageLoadStatus.ProtocolError,
            ClientMessageHistoryHttpStatus.RemoteFailure =>
                ClientMessageLoadStatus.RemoteFailure,
            _ => throw new InvalidOperationException(
                "Unexpected message history HTTP status."),
        };

    private static ClientMessageLoadStatus MapLocalStatus(
        LocalCacheOperationStatus status) =>
        status switch
        {
            LocalCacheOperationStatus.RevokedConversation =>
                ClientMessageLoadStatus.AccessRevoked,
            LocalCacheOperationStatus.TransientFailure =>
                ClientMessageLoadStatus.TransientFailure,
            LocalCacheOperationStatus.ProtocolError or LocalCacheOperationStatus.Conflict =>
                ClientMessageLoadStatus.ProtocolError,
            _ => ClientMessageLoadStatus.LocalCacheFailure,
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
