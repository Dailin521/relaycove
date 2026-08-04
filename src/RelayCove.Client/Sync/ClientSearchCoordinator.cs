using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Search;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientSearchCoordinator : IAsyncDisposable
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 50;
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientSearchHttpTransport transport;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientSearchCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int disposed;

    public ClientSearchCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientSearchCoordinator> logger,
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
                "The local cache must belong to the search account scope.",
                nameof(localCache));
        }

        transport = new ClientSearchHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger);
    }

    public async Task<ClientSearchOutcome> SearchAsync(
        string? keyword,
        Guid? conversationId,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!ClientSearchPolicy.TryNormalizeKeyword(keyword, out var normalizedKeyword) ||
            conversationId == Guid.Empty ||
            limit is < 1 or > MaximumLimit)
        {
            return ClientSearchOutcome.Failure(ClientSearchStatus.ValidationFailed);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        try
        {
            var httpResult = await transport
                .SearchAsync(normalizedKeyword, conversationId, limit, linkedCancellation.Token)
                .ConfigureAwait(false);
            if (httpResult.Status == ClientSearchHttpStatus.AccessRevoked)
            {
                return ClientSearchOutcome.Failure(
                    await RevokeConversationAsync(conversationId!.Value).ConfigureAwait(false));
            }

            if (httpResult.Status != ClientSearchHttpStatus.Success)
            {
                return ClientSearchOutcome.Failure(
                    MapHttpStatus(httpResult.Status),
                    httpResult.RetryAfterSeconds);
            }

            var response = httpResult.Response!;
            if (!TryValidateResponse(response, conversationId, limit))
            {
                logger.LogWarning("A search response failed protocol validation.");
                return ClientSearchOutcome.Failure(ClientSearchStatus.ProtocolError);
            }

            return new ClientSearchOutcome(
                ClientSearchStatus.Completed,
                response.Results.ToList().AsReadOnly(),
                response.HasMore,
                RetryAfterSeconds: null);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientSearchOutcome.Failure(ClientSearchStatus.Canceled);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return ClientSearchOutcome.Failure(ClientSearchStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Searching messages failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientSearchOutcome.Failure(ClientSearchStatus.LocalCacheFailure);
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

    private static bool TryValidateResponse(
        SearchResponse? response,
        Guid? conversationId,
        int limit)
    {
        if (response?.Results is null ||
            response.Results.Count > limit ||
            (response.HasMore && response.Results.Count != limit))
        {
            return false;
        }

        var messageIds = new HashSet<long>();
        long? previousMessageId = null;
        foreach (var result in response.Results)
        {
            if (!ClientSearchPolicy.IsValidResult(result) ||
                !messageIds.Add(result.MessageId) ||
                (previousMessageId.HasValue && previousMessageId.Value <= result.MessageId) ||
                (conversationId.HasValue && result.ConversationId != conversationId.Value))
            {
                return false;
            }

            previousMessageId = result.MessageId;
        }

        return true;
    }

    private async Task<ClientSearchStatus> RevokeConversationAsync(Guid conversationId)
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
                    "Clearing notification state after search revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        return revokeStatus == LocalCacheOperationStatus.RevokedConversation
            ? ClientSearchStatus.AccessRevoked
            : ClientSearchStatus.LocalCacheFailure;
    }

    private static ClientSearchStatus MapHttpStatus(ClientSearchHttpStatus status) =>
        status switch
        {
            ClientSearchHttpStatus.AuthenticationRequired =>
                ClientSearchStatus.AuthenticationRequired,
            ClientSearchHttpStatus.AccessDenied => ClientSearchStatus.AccessDenied,
            ClientSearchHttpStatus.RateLimited => ClientSearchStatus.RateLimited,
            ClientSearchHttpStatus.Timeout => ClientSearchStatus.Timeout,
            ClientSearchHttpStatus.TransientFailure => ClientSearchStatus.TransientFailure,
            ClientSearchHttpStatus.ProtocolError => ClientSearchStatus.ProtocolError,
            ClientSearchHttpStatus.RemoteFailure => ClientSearchStatus.RemoteFailure,
            _ => throw new InvalidOperationException("Unexpected search HTTP status."),
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
