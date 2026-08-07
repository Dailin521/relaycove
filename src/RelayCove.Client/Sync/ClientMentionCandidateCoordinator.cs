using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Mentions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientMentionCandidateCoordinator : IAsyncDisposable
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 50;
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientMentionCandidateHttpTransport transport;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientMentionCandidateCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int disposed;

    public ClientMentionCandidateCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientMentionCandidateCoordinator> logger,
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
                "The local cache must belong to the mention candidate account scope.",
                nameof(localCache));
        }

        transport = new ClientMentionCandidateHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger);
    }

    public async Task<ClientMentionCandidateOutcome> SearchAsync(
        Guid conversationId,
        string? query,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (conversationId == Guid.Empty ||
            !ClientMentionPolicy.IsValidQuery(query) ||
            limit is < 1 or > MaximumLimit)
        {
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.ValidationFailed);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        try
        {
            var httpResult = await transport
                .SearchAsync(
                    conversationId,
                    query!,
                    limit,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (httpResult.Status == ClientMentionCandidateHttpStatus.AccessRevoked)
            {
                return ClientMentionCandidateOutcome.Failure(
                    await RevokeConversationAsync(conversationId).ConfigureAwait(false));
            }

            if (httpResult.Status != ClientMentionCandidateHttpStatus.Success)
            {
                return ClientMentionCandidateOutcome.Failure(
                    MapHttpStatus(httpResult.Status));
            }

            var response = httpResult.Response!;
            if (!TryValidateResponse(response, conversationId, query!, limit))
            {
                logger.LogWarning("A mention candidate response failed protocol validation.");
                return ClientMentionCandidateOutcome.Failure(
                    ClientMentionCandidateStatus.ProtocolError);
            }

            return new ClientMentionCandidateOutcome(
                ClientMentionCandidateStatus.Completed,
                response.Candidates.ToList().AsReadOnly(),
                response.HasMore);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.Canceled);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Searching mention candidates failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.LocalCacheFailure);
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
        MentionCandidateListResponse? response,
        Guid conversationId,
        string query,
        int limit)
    {
        if (response?.Candidates is null ||
            response.ConversationId != conversationId ||
            response.Candidates.Count > limit ||
            (response.HasMore && response.Candidates.Count != limit))
        {
            return false;
        }

        var userIds = new HashSet<Guid>();
        var normalizedUserNames = new HashSet<string>(StringComparer.Ordinal);
        string? previousNormalizedUserName = null;
        Guid previousUserId = Guid.Empty;
        foreach (var candidate in response.Candidates)
        {
            if (!ClientMentionPolicy.IsValidCandidate(candidate, query) ||
                !userIds.Add(candidate.UserId))
            {
                return false;
            }

            var normalizedUserName = candidate.UserName.ToUpperInvariant();
            if (!normalizedUserNames.Add(normalizedUserName) ||
                previousNormalizedUserName is not null &&
                (string.CompareOrdinal(previousNormalizedUserName, normalizedUserName) > 0 ||
                 string.Equals(
                     previousNormalizedUserName,
                     normalizedUserName,
                     StringComparison.Ordinal) &&
                 previousUserId.CompareTo(candidate.UserId) >= 0))
            {
                return false;
            }

            previousNormalizedUserName = normalizedUserName;
            previousUserId = candidate.UserId;
        }

        return true;
    }

    private async Task<ClientMentionCandidateStatus> RevokeConversationAsync(
        Guid conversationId)
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
                    "Clearing notification state after mention candidate revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        return revokeStatus == LocalCacheOperationStatus.RevokedConversation
            ? ClientMentionCandidateStatus.AccessRevoked
            : ClientMentionCandidateStatus.LocalCacheFailure;
    }

    private static ClientMentionCandidateStatus MapHttpStatus(
        ClientMentionCandidateHttpStatus status) =>
        status switch
        {
            ClientMentionCandidateHttpStatus.AuthenticationRequired =>
                ClientMentionCandidateStatus.AuthenticationRequired,
            ClientMentionCandidateHttpStatus.AccessDenied =>
                ClientMentionCandidateStatus.AccessDenied,
            ClientMentionCandidateHttpStatus.TransientFailure =>
                ClientMentionCandidateStatus.TransientFailure,
            ClientMentionCandidateHttpStatus.ProtocolError =>
                ClientMentionCandidateStatus.ProtocolError,
            ClientMentionCandidateHttpStatus.RemoteFailure =>
                ClientMentionCandidateStatus.RemoteFailure,
            _ => throw new InvalidOperationException(
                "Unexpected mention candidate HTTP status."),
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
