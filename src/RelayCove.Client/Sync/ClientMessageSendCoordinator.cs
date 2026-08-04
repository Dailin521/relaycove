using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Mentions;
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
    private readonly ClientAttachmentUploadHttpTransport attachmentUploadTransport;
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
        : this(
            identity,
            senderDisplayName,
            httpClient,
            httpClient,
            authenticationSession,
            localCache,
            logger,
            conversationRevokedAsync)
    {
    }

    public ClientMessageSendCoordinator(
        AccountScopeIdentity identity,
        string senderDisplayName,
        HttpClient httpClient,
        HttpClient attachmentUploadHttpClient,
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
        attachmentUploadTransport = new ClientAttachmentUploadHttpTransport(
            identity,
            attachmentUploadHttpClient ?? throw new ArgumentNullException(nameof(attachmentUploadHttpClient)),
            authenticationSession,
            logger);
    }

    public Task<ClientMessageSendOutcome> SendTextAsync(
        Guid conversationId,
        string? content,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (conversationId == Guid.Empty ||
            replyToMessageId is <= 0 ||
            !ClientTextMessageContentValidator.IsValid(content) ||
            !ClientMentionPolicy.TryCanonicalizeUserIds(
                mentionUserIds ?? NoIds,
                out var canonicalMentionUserIds))
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
            replyToMessageId,
            MentionUserIds: canonicalMentionUserIds,
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

    public Task<ClientMessageSendOutcome> SendAttachmentsAsync(
        Guid conversationId,
        MessageType type,
        IReadOnlyList<ClientAttachmentUploadSource>? sources,
        long? replyToMessageId = null,
        IReadOnlyList<Guid>? mentionUserIds = null,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentSendProgress>? progress = null)
    {
        ThrowIfDisposed();
        if (sources is null)
        {
            return Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed));
        }

        ClientAttachmentUploadSource[] sourceSnapshot;
        try
        {
            sourceSnapshot = sources.ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Snapshotting attachment send sources failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed));
        }

        if (conversationId == Guid.Empty ||
            type is not MessageType.Image and not MessageType.File ||
            sourceSnapshot.Length is < 1 or
                > ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage ||
            sourceSnapshot.Any(static source => source is null) ||
            (type == MessageType.Image && sourceSnapshot.Any(source =>
                !source.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))) ||
            replyToMessageId is <= 0 ||
            !ClientMentionPolicy.TryCanonicalizeUserIds(
                mentionUserIds ?? NoIds,
                out var canonicalMentionUserIds))
        {
            return Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.ValidationFailed));
        }

        var clientMessageId = Guid.NewGuid();
        return StartFlight(
            clientMessageId,
            () => UploadCreateAndSendAsync(
                clientMessageId,
                conversationId,
                type,
                sourceSnapshot,
                replyToMessageId,
                canonicalMentionUserIds,
                cancellationToken,
                progress));
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
        var pendingCommitted = false;
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

            pendingCommitted = true;
            return await SendPersistedAsync(created.Message!, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.Canceled,
                pendingCommitted);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Creating a pending message failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure,
                pendingCommitted);
        }
    }

    private async Task<ClientMessageSendOutcome> UploadCreateAndSendAsync(
        Guid clientMessageId,
        Guid conversationId,
        MessageType type,
        IReadOnlyList<ClientAttachmentUploadSource> sources,
        long? replyToMessageId,
        IReadOnlyList<Guid> mentionUserIds,
        CancellationToken callerCancellation,
        IProgress<ClientAttachmentSendProgress>? progress)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            callerCancellation);
        var storedAttachmentIds = new List<Guid>(sources.Count);
        var progressReporter = progress is null
            ? null
            : new AttachmentSendProgressReporter(sources, progress, logger);
        try
        {
            var accessStatus = localCache.GetConversationAccessStatus(conversationId);
            if (accessStatus != LocalCacheOperationStatus.Ready)
            {
                return ClientMessageSendOutcome.Failure(
                    MapLocalFailure(accessStatus));
            }

            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var source = sources[sourceIndex];
                progressReporter?.ReportUploading(sourceIndex, bytesCopied: 0, force: true);
                var upload = await attachmentUploadTransport
                    .UploadAsync(
                        source,
                        linkedCancellation.Token,
                        progressReporter is null
                            ? null
                            : bytesCopied => progressReporter.ReportUploading(
                                sourceIndex,
                                bytesCopied,
                                force: false))
                    .ConfigureAwait(false);
                if (upload.Status != ClientAttachmentUploadHttpStatus.Success)
                {
                    return ClientMessageSendOutcome.Failure(MapUploadFailure(upload.Status));
                }

                var reservation = await localCache
                    .StoreUnboundAttachmentReservationAsync(
                        upload.Attachment!,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                if (reservation.Status != LocalCacheOperationStatus.Ready ||
                    reservation.Result != LocalAttachmentReservationResult.Stored)
                {
                    return ClientMessageSendOutcome.Failure(
                        reservation.Result is LocalAttachmentReservationResult.Conflict or
                            LocalAttachmentReservationResult.AlreadyExists
                            ? ClientMessageSendStatus.ProtocolError
                            : MapLocalFailure(reservation.Status));
                }

                storedAttachmentIds.Add(upload.Attachment!.Id);
                progressReporter?.CompleteAttachment(sourceIndex);
            }

            var canonicalAttachmentIds = storedAttachmentIds
                .OrderBy(static id => id)
                .ToArray();
            if (canonicalAttachmentIds.Distinct().Count() != canonicalAttachmentIds.Length)
            {
                return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.ProtocolError);
            }

            progressReporter?.ReportFinalizing();

            var pending = new PendingMessage(
                clientMessageId,
                conversationId,
                identity.UserId,
                senderDisplayName,
                type,
                Content: null,
                replyToMessageId,
                mentionUserIds,
                DateTimeOffset.UtcNow)
            {
                AttachmentIds = canonicalAttachmentIds,
            };
            var outcome = await CreateAndSendAsync(pending, linkedCancellation.Token)
                .ConfigureAwait(false);
            if (outcome.PendingCommitted)
            {
                storedAttachmentIds.Clear();
            }

            return outcome;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Uploading attachment message inputs failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientMessageSendOutcome.Failure(ClientMessageSendStatus.LocalCacheFailure);
        }
        finally
        {
            if (storedAttachmentIds.Count != 0)
            {
                await CleanupUnboundReservationsAsync(storedAttachmentIds).ConfigureAwait(false);
            }
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
                "Preparing a pending message retry failed; errorType={ErrorType}.",
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
            pending.AttachmentIds,
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
                "Sending a pending message failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            await MarkFailedAsync(pending).ConfigureAwait(false);
            return ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.LocalCacheFailure,
                pendingCommitted: true);
        }

        if (httpResult.Status == ClientMessageSendHttpStatus.Success)
        {
            LocalCacheMergeOutcome merge;
            try
            {
                merge = await localCache
                    .MergeIncomingMessageAsync(
                        httpResult.Message!,
                        LocalMessageIngestionContext.Background(
                            IncomingMessageSource.SendResponse),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Merging a message send response failed; errorType={ErrorType}.",
                    exception.GetType().Name);
                await MarkFailedAsync(pending).ConfigureAwait(false);
                return ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.LocalCacheFailure,
                    pendingCommitted: true);
            }
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
                "Marking a pending message failed; errorType={ErrorType}.",
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

    private static ClientMessageSendStatus MapUploadFailure(
        ClientAttachmentUploadHttpStatus status) =>
        status switch
        {
            ClientAttachmentUploadHttpStatus.AuthenticationRequired =>
                ClientMessageSendStatus.AuthenticationRequired,
            ClientAttachmentUploadHttpStatus.ValidationFailed =>
                ClientMessageSendStatus.ValidationFailed,
            ClientAttachmentUploadHttpStatus.AttachmentTooLarge =>
                ClientMessageSendStatus.AttachmentTooLarge,
            ClientAttachmentUploadHttpStatus.SourceUnavailable =>
                ClientMessageSendStatus.SourceUnavailable,
            ClientAttachmentUploadHttpStatus.TransientFailure =>
                ClientMessageSendStatus.TransientFailure,
            ClientAttachmentUploadHttpStatus.ProtocolError =>
                ClientMessageSendStatus.ProtocolError,
            ClientAttachmentUploadHttpStatus.RemoteFailure =>
                ClientMessageSendStatus.RemoteFailure,
            ClientAttachmentUploadHttpStatus.Canceled => ClientMessageSendStatus.Canceled,
            _ => ClientMessageSendStatus.LocalCacheFailure,
        };

    private async Task CleanupUnboundReservationsAsync(IReadOnlyCollection<Guid> attachmentIds)
    {
        try
        {
            var status = await localCache
                .RemoveUnboundAttachmentReservationsAsync(attachmentIds, CancellationToken.None)
                .ConfigureAwait(false);
            if (status != LocalCacheOperationStatus.Ready)
            {
                logger.LogWarning(
                    "Cleaning unbound attachment reservations failed; status={Status}.",
                    status);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Cleaning unbound attachment reservations failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private sealed class AttachmentSendProgressReporter
    {
        private readonly IReadOnlyList<ClientAttachmentUploadSource> sources;
        private readonly IProgress<ClientAttachmentSendProgress> progress;
        private readonly ILogger logger;
        private readonly long totalBytes;
        private long completedBytes;
        private long highestCurrentAttemptBytes;
        private int currentIndex = -1;
        private int lastAttachmentIndex = -1;
        private int lastPercent = -1;

        public AttachmentSendProgressReporter(
            IReadOnlyList<ClientAttachmentUploadSource> sources,
            IProgress<ClientAttachmentSendProgress> progress,
            ILogger logger)
        {
            this.sources = sources;
            this.progress = progress;
            this.logger = logger;
            totalBytes = sources.Aggregate(
                0L,
                static (total, source) => checked(total + source.Size));
        }

        public void ReportUploading(int sourceIndex, long bytesCopied, bool force)
        {
            if (sourceIndex is < 0 or >= 10 || sourceIndex >= sources.Count)
            {
                return;
            }

            if (currentIndex != sourceIndex)
            {
                currentIndex = sourceIndex;
                highestCurrentAttemptBytes = 0;
                force = true;
            }

            var boundedCurrentBytes = Math.Clamp(bytesCopied, 0, sources[sourceIndex].Size);
            highestCurrentAttemptBytes = Math.Max(highestCurrentAttemptBytes, boundedCurrentBytes);
            var aggregateBytes = Math.Clamp(
                checked(completedBytes + highestCurrentAttemptBytes),
                0,
                totalBytes);
            var percent = (int)((aggregateBytes * 100) / totalBytes);
            if (!force && lastAttachmentIndex == sourceIndex && percent == lastPercent)
            {
                return;
            }

            Report(new ClientAttachmentSendProgress(
                ClientAttachmentSendProgressStage.Uploading,
                sourceIndex + 1,
                sources.Count,
                aggregateBytes,
                totalBytes,
                percent));
            lastAttachmentIndex = sourceIndex;
            lastPercent = percent;
        }

        public void CompleteAttachment(int sourceIndex)
        {
            if (sourceIndex != currentIndex)
            {
                return;
            }

            completedBytes = checked(completedBytes + sources[sourceIndex].Size);
            highestCurrentAttemptBytes = 0;
            var percent = (int)((completedBytes * 100) / totalBytes);
            if (lastAttachmentIndex != sourceIndex || lastPercent != percent)
            {
                Report(new ClientAttachmentSendProgress(
                    ClientAttachmentSendProgressStage.Uploading,
                    sourceIndex + 1,
                    sources.Count,
                    completedBytes,
                    totalBytes,
                    percent));
                lastAttachmentIndex = sourceIndex;
                lastPercent = percent;
            }
        }

        public void ReportFinalizing() =>
            Report(new ClientAttachmentSendProgress(
                ClientAttachmentSendProgressStage.Finalizing,
                sources.Count,
                sources.Count,
                totalBytes,
                totalBytes,
                percent: 100));

        private void Report(ClientAttachmentSendProgress value)
        {
            try
            {
                progress.Report(value);
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                logger.LogWarning(
                    "Attachment send progress receiver failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private void RemoveFlight(Guid clientMessageId)
    {
        lock (flightGate)
        {
            flights.Remove(clientMessageId);
        }
    }

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

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
