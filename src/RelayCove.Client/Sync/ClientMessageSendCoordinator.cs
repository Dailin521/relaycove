using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed class ClientMessageSendCoordinator : IAsyncDisposable
{
    private static readonly IReadOnlyList<Guid> NoIds = Array.Empty<Guid>();
    private readonly object flightGate = new();
    private readonly AccountScopeIdentity identity;
    private readonly string senderDisplayName;
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientMessageSendHttpTransport transport;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientMessageSendCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly TaskCompletionSource disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, Task<ClientMessageSendOutcome>> flights = [];
    private int disposed;

    public ClientMessageSendCoordinator(
        AccountScopeIdentity identity,
        string senderDisplayName,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientMessageSendCoordinator> logger,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentNullException.ThrowIfNull(senderDisplayName);
        this.senderDisplayName = senderDisplayName;
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.conversationRevokedAsync = conversationRevokedAsync ??
            (static (_, _) => Task.CompletedTask);
        if (!string.Equals(identity.Id, localCache.Identity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The local cache must belong to the message send account scope.",
                nameof(localCache));
        }

        transport = new ClientMessageSendHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger);
    }

    public Task<ClientMessageSendOutcome> SendTextAsync(
        Guid conversationId,
        string? content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (conversationId == Guid.Empty ||
            !ClientTextMessageContentValidator.IsValid(content))
        {
            return Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed));
        }

        var pending = new PendingMessage(
            Guid.NewGuid(),
            conversationId,
            identity.UserId,
            senderDisplayName,
            MessageType.Text,
            content,
            ReplyToMessageId: null,
            MentionUserIds: NoIds,
            DateTimeOffset.UtcNow);
        return StartFlight(
            pending.ClientMessageId,
            () => CreateAndSendAsync(pending, cancellationToken));
    }

    public Task<ClientMessageSendOutcome> RetryAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (conversationId == Guid.Empty || clientMessageId == Guid.Empty)
        {
            return Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed));
        }

        return StartFlight(
            clientMessageId,
            () => PrepareAndSendAsync(
                conversationId,
                clientMessageId,
                cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return new ValueTask(disposeCompletion.Task);
        }

        return new ValueTask(DisposeCoreAsync());
    }

    private Task<ClientMessageSendOutcome> StartFlight(
        Guid clientMessageId,
        Func<Task<ClientMessageSendOutcome>> start)
    {
        lock (flightGate)
        {
            ThrowIfDisposed();
            if (flights.TryGetValue(clientMessageId, out var existing))
            {
                return existing;
            }

            var flight = start();
            flights.Add(clientMessageId, flight);
            _ = flight.ContinueWith(
                static (_, state) =>
                {
                    var removal = (FlightRemoval)state!;
                    removal.Owner.RemoveFlight(removal.ClientMessageId);
                },
                new FlightRemoval(this, clientMessageId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return flight;
        }
    }

    private async Task<ClientMessageSendOutcome> CreateAndSendAsync(
        PendingMessage pending,
        CancellationToken callerCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            callerCancellation);
        try
        {
            var created = await localCache
                .CreatePendingMessageAsync(pending, linkedCancellation.Token)
                .ConfigureAwait(false);
            if (created.Status != LocalCacheOperationStatus.Ready ||
                created.Result != LocalPendingMessageMutationResult.Created)
            {
                return ClientMessageSendOutcome.Failure(MapCreateFailure(created));
            }

            return await SendPersistedAsync(created.Message!, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Creating a pending Text message failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.LocalCacheFailure);
        }
    }

    private async Task<ClientMessageSendOutcome> PrepareAndSendAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken callerCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            callerCancellation);
        try
        {
            var prepared = await localCache
                .PreparePendingMessageRetryAsync(
                    conversationId,
                    clientMessageId,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (prepared.Status != LocalCacheOperationStatus.Ready ||
                prepared.Result != LocalPendingMessageMutationResult.PreparedRetry)
            {
                return ClientMessageSendOutcome.Failure(MapRetryFailure(prepared));
            }

            return await SendPersistedAsync(prepared.Message!, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Preparing a pending Text message retry failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.LocalCacheFailure);
        }
    }

    private async Task<ClientMessageSendOutcome> SendPersistedAsync(
        LocalPendingMessage pending,
        CancellationToken cancellationToken)
    {
        var request = new SendMessageRequest(
            pending.ClientMessageId,
            pending.ConversationId,
            pending.Type,
            pending.Content,
            pending.ReplyToMessageId,
            AttachmentIds: NoIds,
            pending.MentionUserIds);
        ClientMessageSendHttpResult httpResult;
        try
        {
            httpResult = await transport.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Sending a pending Text message failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            await MarkFailedAsync(pending).ConfigureAwait(false);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure,
                pendingCommitted: true);
        }

        if (httpResult.Status == ClientMessageSendHttpStatus.Success)
        {
            var merge = await localCache
                .MergeIncomingMessageAsync(
                    httpResult.Message!,
                    LocalMessageIngestionContext.Background(
                        IncomingMessageSource.SendResponse),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (merge.Status == LocalCacheOperationStatus.Ready &&
                merge.Result is IncomingMessageMergeResult.PendingPromoted or
                    IncomingMessageMergeResult.Duplicate)
            {
                return new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true);
            }

            await MarkFailedAsync(pending).ConfigureAwait(false);
            return ClientMessageSendOutcome.Failure(
                merge.Result == IncomingMessageMergeResult.Conflict
                    ? ClientMessageSendStatus.ProtocolError
                    : ClientMessageSendStatus.LocalCacheFailure,
                pendingCommitted: true);
        }

        if (httpResult.Status == ClientMessageSendHttpStatus.AccessRevoked)
        {
            return await RevokeConversationAsync(pending.ConversationId)
                .ConfigureAwait(false);
        }

        await MarkFailedAsync(pending).ConfigureAwait(false);
        return ClientMessageSendOutcome.Failure(
            MapHttpFailure(httpResult.Status),
            pendingCommitted: true);
    }

    private async Task MarkFailedAsync(LocalPendingMessage pending)
    {
        try
        {
            _ = await localCache
                .MarkPendingMessageFailedAsync(
                    pending.ConversationId,
                    pending.ClientMessageId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Marking a pending Text message failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private async Task<ClientMessageSendOutcome> RevokeConversationAsync(Guid conversationId)
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
                    "Clearing notification state after send revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        return ClientMessageSendOutcome.Failure(
            revokeStatus == LocalCacheOperationStatus.RevokedConversation
                ? ClientMessageSendStatus.AccessRevoked
                : ClientMessageSendStatus.LocalCacheFailure,
            pendingCommitted: true);
    }

    private static ClientMessageSendStatus MapCreateFailure(
        LocalPendingMessageMutationOutcome outcome) =>
        outcome.Result switch
        {
            LocalPendingMessageMutationResult.CapacityExceeded =>
                ClientMessageSendStatus.CapacityExceeded,
            LocalPendingMessageMutationResult.Conflict =>
                ClientMessageSendStatus.ProtocolError,
            _ => MapLocalFailure(outcome.Status),
        };

    private static ClientMessageSendStatus MapRetryFailure(
        LocalPendingMessageMutationOutcome outcome) =>
        outcome.Result switch
        {
            LocalPendingMessageMutationResult.AlreadySent =>
                ClientMessageSendStatus.Completed,
            LocalPendingMessageMutationResult.NotFound or
                LocalPendingMessageMutationResult.NotRetryable =>
                ClientMessageSendStatus.NotRetryable,
            _ => MapLocalFailure(outcome.Status),
        };

    private static ClientMessageSendStatus MapLocalFailure(
        LocalCacheOperationStatus status) =>
        status == LocalCacheOperationStatus.RevokedConversation
            ? ClientMessageSendStatus.AccessRevoked
            : ClientMessageSendStatus.LocalCacheFailure;

    private static ClientMessageSendStatus MapHttpFailure(
        ClientMessageSendHttpStatus status) =>
        status switch
        {
            ClientMessageSendHttpStatus.AuthenticationRequired =>
                ClientMessageSendStatus.AuthenticationRequired,
            ClientMessageSendHttpStatus.AccessDenied => ClientMessageSendStatus.AccessDenied,
            ClientMessageSendHttpStatus.ValidationFailed =>
                ClientMessageSendStatus.ValidationFailed,
            ClientMessageSendHttpStatus.IdempotencyConflict =>
                ClientMessageSendStatus.IdempotencyConflict,
            ClientMessageSendHttpStatus.TransientFailure =>
                ClientMessageSendStatus.TransientFailure,
            ClientMessageSendHttpStatus.ProtocolError =>
                ClientMessageSendStatus.ProtocolError,
            ClientMessageSendHttpStatus.RemoteFailure =>
                ClientMessageSendStatus.RemoteFailure,
            ClientMessageSendHttpStatus.Canceled => ClientMessageSendStatus.Canceled,
            _ => ClientMessageSendStatus.LocalCacheFailure,
        };

    private void RemoveFlight(Guid clientMessageId)
    {
        lock (flightGate)
        {
            flights.Remove(clientMessageId);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            lifetimeCancellation.Cancel();
            Task[] activeFlights;
            lock (flightGate)
            {
                activeFlights = [.. flights.Values];
            }

            if (activeFlights.Length != 0)
            {
                await Task.WhenAll(activeFlights).ConfigureAwait(false);
            }

            disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            disposeCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            lifetimeCancellation.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private sealed record FlightRemoval(
        ClientMessageSendCoordinator Owner,
        Guid ClientMessageId);
}
