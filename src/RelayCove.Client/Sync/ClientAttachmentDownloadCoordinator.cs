using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal sealed class ClientAttachmentDownloadCoordinator :
    IClientAttachmentDownloadCoordinator
{
    private readonly AccountScopedLocalCache localCache;
    private readonly IClientAttachmentCacheStore cacheStore;
    private readonly ClientAttachmentDownloadHttpTransport transport;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientAttachmentDownloadCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly ConcurrentDictionary<
        AttachmentFlightKey,
        CancellationTokenSource> activeFlights = new();
    private readonly ConcurrentDictionary<Guid, byte> pendingConversationPurges = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private int recoveryCompleted;
    private int disposed;

    internal ClientAttachmentDownloadCoordinator(
        AccountScopedLocalCache localCache,
        IClientAttachmentCacheStore cacheStore,
        ClientAttachmentDownloadHttpTransport transport,
        ILogger<ClientAttachmentDownloadCoordinator> logger,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null)
    {
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.conversationRevokedAsync = conversationRevokedAsync ??
            (static (_, _) => Task.CompletedTask);
        localCache.AttachmentDownloadCancellationRequested += CancelConversationDownloads;
        localCache.AttachmentCachePurged += PurgeConversationCacheAsync;
    }

    public async Task<ClientAttachmentCacheRecoveryStatus> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref recoveryCompleted) != 0)
        {
            return ClientAttachmentCacheRecoveryStatus.Ready;
        }

        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref recoveryCompleted) != 0)
            {
                return ClientAttachmentCacheRecoveryStatus.Ready;
            }

            var database = await localCache
                .PrepareAttachmentCacheRecoveryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (database.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentCacheRecoveryStatus.LocalCacheFailure;
            }

            var referencedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in database.DownloadedAttachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CreateKeyFromRecord(record);
                var validation = await cacheStore
                    .ValidateAsync(
                        record.LocalPath!,
                        key,
                        record.Attachment.Size,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (validation.Status == ClientAttachmentCacheStoreStatus.Ready &&
                    validation.IsValid)
                {
                    referencedPaths.Add(record.LocalPath!);
                    continue;
                }

                if (validation.Status == ClientAttachmentCacheStoreStatus.StorageFailure)
                {
                    return ClientAttachmentCacheRecoveryStatus.StorageFailure;
                }

                var invalidated = await localCache
                    .InvalidateRecoveredAttachmentAsync(
                        record.ConversationId,
                        record.Attachment.Id,
                        record.LocalPath!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (invalidated != LocalCacheOperationStatus.Ready)
                {
                    return ClientAttachmentCacheRecoveryStatus.LocalCacheFailure;
                }
            }

            var enumeration = await cacheStore
                .EnumerateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (enumeration.Status != ClientAttachmentCacheStoreStatus.Ready)
            {
                return ClientAttachmentCacheRecoveryStatus.StorageFailure;
            }

            foreach (var entry in enumeration.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Kind == ClientAttachmentCacheStoreEntryKind.Final &&
                    referencedPaths.Contains(entry.RelativePath))
                {
                    continue;
                }

                var deleted = await cacheStore
                    .DeleteAsync(entry.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
                if (deleted.Status is not ClientAttachmentCacheStoreStatus.Ready and
                    not ClientAttachmentCacheStoreStatus.NotFound)
                {
                    return ClientAttachmentCacheRecoveryStatus.StorageFailure;
                }
            }

            Volatile.Write(ref recoveryCompleted, 1);
            return ClientAttachmentCacheRecoveryStatus.Ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Attachment cache recovery failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentCacheRecoveryStatus.LocalCacheFailure;
        }
        finally
        {
            recoveryGate.Release();
        }
    }

    public async Task<ClientAttachmentDownloadOutcome> DownloadAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default,
        IProgress<ClientAttachmentDownloadProgress>? progress = null)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ThrowIfDisposed();
        if (Volatile.Read(ref recoveryCompleted) == 0)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure);
        }

        var flightKey = new AttachmentFlightKey(conversationId, attachmentId);
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        if (!activeFlights.TryAdd(flightKey, linkedCancellation))
        {
            linkedCancellation.Dispose();
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.InProgress);
        }

        try
        {
            return await DownloadCoreAsync(
                    conversationId,
                    attachmentId,
                    linkedCancellation.Token,
                    progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                MapCancellationStatus(conversationId));
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Attachment download coordination failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure);
        }
        finally
        {
            activeFlights.TryRemove(flightKey, out _);
            linkedCancellation.Dispose();
            await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            localCache.AttachmentDownloadCancellationRequested -= CancelConversationDownloads;
            localCache.AttachmentCachePurged -= PurgeConversationCacheAsync;
            lifetimeCancellation.Cancel();
            foreach (var cancellation in activeFlights.Values)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A completed flight may dispose concurrently with runtime shutdown.
                }
            }

            lifetimeCancellation.Dispose();
            recoveryGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadCoordinator)} {{ " +
        "LocalCache = [REDACTED], CacheStore = [REDACTED], Transport = [REDACTED] }";

    private async Task<ClientAttachmentDownloadOutcome> DownloadCoreAsync(
        Guid conversationId,
        Guid attachmentId,
        CancellationToken cancellationToken,
        IProgress<ClientAttachmentDownloadProgress>? progress)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var claim = await localCache
                .ClaimAttachmentDownloadAsync(
                    conversationId,
                    attachmentId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (claim.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentDownloadOutcome.Failure(
                    MapLocalStatus(claim.Status));
            }

            switch (claim.Result)
            {
                case LocalAttachmentDownloadClaimResult.InProgress:
                    return ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.InProgress);
                case LocalAttachmentDownloadClaimResult.AttachmentUnavailable:
                    return ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.AttachmentUnavailable);
                case LocalAttachmentDownloadClaimResult.AlreadyDownloaded:
                    {
                        var cached = await ValidateExistingAsync(
                                claim.Record!,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (cached is not null)
                        {
                            return cached;
                        }

                        continue;
                    }
                case LocalAttachmentDownloadClaimResult.Claimed:
                    return await DownloadClaimedAsync(
                            claim.Record!,
                            cancellationToken,
                            progress)
                        .ConfigureAwait(false);
                default:
                    return ClientAttachmentDownloadOutcome.Failure(
                        ClientAttachmentDownloadStatus.ProtocolError);
            }
        }

        return ClientAttachmentDownloadOutcome.Failure(
            ClientAttachmentDownloadStatus.LocalCacheFailure);
    }

    private async Task<ClientAttachmentDownloadOutcome?> ValidateExistingAsync(
        LocalAttachmentDownloadRecord record,
        CancellationToken cancellationToken)
    {
        var key = CreateKeyFromRecord(record);
        var validation = await cacheStore
            .ValidateAsync(
                record.LocalPath!,
                key,
                record.Attachment.Size,
                cancellationToken)
            .ConfigureAwait(false);
        if (validation.Status == ClientAttachmentCacheStoreStatus.Ready &&
            validation.IsValid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentAccess = localCache.GetConversationAccessStatus(record.ConversationId);
            return currentAccess == LocalCacheOperationStatus.Ready
                ? new ClientAttachmentDownloadOutcome(
                    ClientAttachmentDownloadStatus.AlreadyDownloaded,
                    record.LocalPath)
                : ClientAttachmentDownloadOutcome.Failure(MapLocalStatus(currentAccess));
        }

        if (validation.Status == ClientAttachmentCacheStoreStatus.StorageFailure)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure);
        }

        var invalidated = await localCache
            .InvalidateDownloadedAttachmentAsync(
                record.ConversationId,
                record.Attachment.Id,
                record.LocalPath!,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (invalidated != LocalCacheOperationStatus.Ready)
        {
            return ClientAttachmentDownloadOutcome.Failure(
                MapLocalStatus(invalidated));
        }

        var deleted = await cacheStore
            .DeleteAsync(record.LocalPath!, CancellationToken.None)
            .ConfigureAwait(false);
        return deleted.Status is ClientAttachmentCacheStoreStatus.Ready or
            ClientAttachmentCacheStoreStatus.NotFound
                ? null
                : ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.LocalCacheFailure);
    }

    private async Task<ClientAttachmentDownloadOutcome> DownloadClaimedAsync(
        LocalAttachmentDownloadRecord record,
        CancellationToken cancellationToken,
        IProgress<ClientAttachmentDownloadProgress>? progress)
    {
        ClientAttachmentCacheStoreStagingFile? stagingFile = null;
        var resolved = false;
        try
        {
            var staging = await cacheStore
                .CreateStagingAsync(
                    record.ConversationId,
                    record.Attachment.Id,
                    record.Attachment.Size,
                    cancellationToken)
                .ConfigureAwait(false);
            if (staging.Status != ClientAttachmentCacheStoreStatus.Ready ||
                staging.StagingFile is null)
            {
                resolved = true;
                var failedStatus = await FailClaimAsync(record, canceled: false)
                    .ConfigureAwait(false);
                return ClientAttachmentDownloadOutcome.Failure(
                    failedStatus == LocalCacheOperationStatus.RevokedConversation
                        ? ClientAttachmentDownloadStatus.AccessRevoked
                        : staging.Status == ClientAttachmentCacheStoreStatus.QuotaExceeded
                        ? ClientAttachmentDownloadStatus.QuotaExceeded
                        : ClientAttachmentDownloadStatus.LocalCacheFailure);
            }

            stagingFile = staging.StagingFile;
            var download = await transport
                .DownloadAsync(
                    record.Attachment,
                    stagingFile.Stream,
                    cancellationToken,
                    progress is null ? null : progress.Report)
                .ConfigureAwait(false);
            if (download.Status == ClientAttachmentDownloadHttpStatus.AccessRevoked)
            {
                resolved = true;
                return await RevokeConversationAsync(record.ConversationId)
                    .ConfigureAwait(false);
            }

            if (download.Status != ClientAttachmentDownloadHttpStatus.Success)
            {
                resolved = true;
                var failedStatus = await FailClaimAsync(
                        record,
                        download.Status == ClientAttachmentDownloadHttpStatus.Canceled)
                    .ConfigureAwait(false);
                return ClientAttachmentDownloadOutcome.Failure(
                    failedStatus == LocalCacheOperationStatus.RevokedConversation
                        ? ClientAttachmentDownloadStatus.AccessRevoked
                        : MapHttpStatus(download.Status));
            }

            var published = await cacheStore
                .PublishAsync(stagingFile, download.Sha256!, CancellationToken.None)
                .ConfigureAwait(false);
            if (published.Status is not ClientAttachmentCacheStoreStatus.Ready and
                not ClientAttachmentCacheStoreStatus.AlreadyPublished ||
                published.RelativePath is null)
            {
                resolved = true;
                var failedStatus = await FailClaimAsync(record, canceled: false)
                    .ConfigureAwait(false);
                return ClientAttachmentDownloadOutcome.Failure(
                    failedStatus == LocalCacheOperationStatus.RevokedConversation
                        ? ClientAttachmentDownloadStatus.AccessRevoked
                        : published.Status switch
                        {
                            ClientAttachmentCacheStoreStatus.QuotaExceeded =>
                                ClientAttachmentDownloadStatus.QuotaExceeded,
                            ClientAttachmentCacheStoreStatus.ValidationFailed =>
                                ClientAttachmentDownloadStatus.ProtocolError,
                            _ => ClientAttachmentDownloadStatus.LocalCacheFailure,
                        });
            }

            var completed = await localCache
                .CompleteAttachmentDownloadAsync(
                    record.ConversationId,
                    record.Attachment.Id,
                    published.RelativePath,
                    CancellationToken.None)
                .ConfigureAwait(false);
            resolved = true;
            if (completed != LocalCacheOperationStatus.Ready)
            {
                await cacheStore
                    .DeleteAsync(published.RelativePath, CancellationToken.None)
                    .ConfigureAwait(false);
                if (completed is not LocalCacheOperationStatus.RevokedConversation and
                    not LocalCacheOperationStatus.FatalScope)
                {
                    await FailClaimAsync(record, canceled: false).ConfigureAwait(false);
                }
                return ClientAttachmentDownloadOutcome.Failure(MapLocalStatus(completed));
            }

            var currentAccess = localCache.GetConversationAccessStatus(record.ConversationId);
            return currentAccess == LocalCacheOperationStatus.Ready
                ? new ClientAttachmentDownloadOutcome(
                    ClientAttachmentDownloadStatus.Completed,
                    published.RelativePath)
                : ClientAttachmentDownloadOutcome.Failure(MapLocalStatus(currentAccess));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resolved = true;
            var failedStatus = await FailClaimAsync(record, canceled: true)
                .ConfigureAwait(false);
            return ClientAttachmentDownloadOutcome.Failure(
                failedStatus == LocalCacheOperationStatus.RevokedConversation
                    ? ClientAttachmentDownloadStatus.AccessRevoked
                    : ClientAttachmentDownloadStatus.Canceled);
        }
        catch (Exception exception)
        {
            resolved = true;
            logger.LogError(
                "A claimed attachment download failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            await FailClaimAsync(record, canceled: false).ConfigureAwait(false);
            return ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.LocalCacheFailure);
        }
        finally
        {
            if (!resolved)
            {
                await FailClaimAsync(record, canceled: false).ConfigureAwait(false);
            }

            if (stagingFile is not null)
            {
                await stagingFile.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<LocalCacheOperationStatus> FailClaimAsync(
        LocalAttachmentDownloadRecord record,
        bool canceled)
    {
        try
        {
            return await localCache
                .FailAttachmentDownloadAsync(
                    record.ConversationId,
                    record.Attachment.Id,
                    canceled,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Resolving a failed attachment download claim failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private async Task<ClientAttachmentDownloadOutcome> RevokeConversationAsync(
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
                    "Clearing notification state after attachment download revocation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        return ClientAttachmentDownloadOutcome.Failure(
            revokeStatus == LocalCacheOperationStatus.RevokedConversation
                ? ClientAttachmentDownloadStatus.AccessRevoked
                : ClientAttachmentDownloadStatus.LocalCacheFailure);
    }

    private void CancelConversationDownloads(Guid conversationId)
    {
        foreach (var flight in activeFlights)
        {
            if (flight.Key.ConversationId != conversationId)
            {
                continue;
            }

            try
            {
                flight.Value.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion may race a durable revocation notification.
            }
        }
    }

    private async Task PurgeConversationCacheAsync(Guid conversationId)
    {
        var outcome = await cacheStore
            .DeleteConversationAsync(conversationId, CancellationToken.None)
            .ConfigureAwait(false);
        if (outcome.Status == ClientAttachmentCacheStoreStatus.Ready)
        {
            pendingConversationPurges.TryRemove(conversationId, out _);
            return;
        }

        pendingConversationPurges.TryAdd(conversationId, 0);
        await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
    }

    private async Task RetryPendingPurgeIfQuiescentAsync(Guid conversationId)
    {
        if (!pendingConversationPurges.ContainsKey(conversationId) ||
            activeFlights.Keys.Any(key => key.ConversationId == conversationId))
        {
            return;
        }

        try
        {
            var retry = await cacheStore
                .DeleteConversationAsync(conversationId, CancellationToken.None)
                .ConfigureAwait(false);
            if (retry.Status == ClientAttachmentCacheStoreStatus.Ready)
            {
                pendingConversationPurges.TryRemove(conversationId, out _);
            }
            else
            {
                logger.LogWarning(
                    "A quiescent revoked-conversation attachment cache purge retry failed; " +
                    "status={Status}.",
                    retry.Status);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "A quiescent revoked-conversation attachment cache purge retry failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private ClientAttachmentDownloadStatus MapCancellationStatus(Guid conversationId)
    {
        try
        {
            return localCache.GetConversationAccessStatus(conversationId) ==
                LocalCacheOperationStatus.RevokedConversation
                    ? ClientAttachmentDownloadStatus.AccessRevoked
                    : ClientAttachmentDownloadStatus.Canceled;
        }
        catch (Exception)
        {
            return ClientAttachmentDownloadStatus.Canceled;
        }
    }

    private static ClientAttachmentDownloadStatus MapLocalStatus(
        LocalCacheOperationStatus status) =>
        status switch
        {
            LocalCacheOperationStatus.RevokedConversation =>
                ClientAttachmentDownloadStatus.AccessRevoked,
            LocalCacheOperationStatus.TransientFailure =>
                ClientAttachmentDownloadStatus.TransientFailure,
            LocalCacheOperationStatus.ProtocolError or LocalCacheOperationStatus.Conflict =>
                ClientAttachmentDownloadStatus.ProtocolError,
            LocalCacheOperationStatus.UnknownConversation =>
                ClientAttachmentDownloadStatus.AttachmentUnavailable,
            _ => ClientAttachmentDownloadStatus.LocalCacheFailure,
        };

    private static ClientAttachmentDownloadStatus MapHttpStatus(
        ClientAttachmentDownloadHttpStatus status) =>
        status switch
        {
            ClientAttachmentDownloadHttpStatus.AuthenticationRequired =>
                ClientAttachmentDownloadStatus.AuthenticationRequired,
            ClientAttachmentDownloadHttpStatus.AccessRevoked =>
                ClientAttachmentDownloadStatus.AccessRevoked,
            ClientAttachmentDownloadHttpStatus.AccessDenied =>
                ClientAttachmentDownloadStatus.AccessDenied,
            ClientAttachmentDownloadHttpStatus.Canceled =>
                ClientAttachmentDownloadStatus.Canceled,
            ClientAttachmentDownloadHttpStatus.TransientFailure =>
                ClientAttachmentDownloadStatus.TransientFailure,
            ClientAttachmentDownloadHttpStatus.ProtocolError =>
                ClientAttachmentDownloadStatus.ProtocolError,
            ClientAttachmentDownloadHttpStatus.RemoteFailure =>
                ClientAttachmentDownloadStatus.RemoteFailure,
            _ => ClientAttachmentDownloadStatus.LocalCacheFailure,
        };

    private static ClientAttachmentCacheStoreKey CreateKeyFromRecord(
        LocalAttachmentDownloadRecord record)
    {
        var parts = record.LocalPath!.Split('.', StringSplitOptions.None);
        if (parts.Length != 4)
        {
            throw new InvalidDataException(
                "The attachment cache record does not contain a managed path.");
        }

        return new ClientAttachmentCacheStoreKey(
            record.ConversationId,
            record.Attachment.Id,
            parts[2]);
    }

    private static void ValidateGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private readonly record struct AttachmentFlightKey(
        Guid ConversationId,
        Guid AttachmentId);
}
