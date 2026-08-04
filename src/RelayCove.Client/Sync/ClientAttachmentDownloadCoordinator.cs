using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Attachments;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal sealed class ClientAttachmentDownloadCoordinator :
    IClientAttachmentDownloadCoordinator
{
    private static readonly TimeSpan ImageProcessingWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, AttachmentImageScopeState>
        ProcessImageScopeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid coordinatorInstanceId = Guid.NewGuid();
    private readonly AccountScopedLocalCache localCache;
    private readonly IClientAttachmentCacheStore cacheStore;
    private readonly ClientAttachmentDownloadHttpTransport transport;
    private readonly IWindowsAttachmentShell attachmentShell;
    private readonly Func<Guid, CancellationToken, Task> conversationRevokedAsync;
    private readonly ILogger<ClientAttachmentDownloadCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly ConcurrentDictionary<
        AttachmentFlightKey,
        CancellationTokenSource> activeFlights = new();
    private readonly ConcurrentDictionary<
        AttachmentFlightKey,
        AttachmentRevealFlight> activeReveals = new();
    private readonly ConcurrentDictionary<
        AttachmentImageFlightKey,
        AttachmentImageFlight> activeImages;
    private readonly ConcurrentDictionary<Guid, byte> pendingConversationPurges = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private readonly SemaphoreSlim imageProcessingGate;
    private readonly ClientAttachmentImageDecodeAsync decodeImageAsync;
    private readonly Action<Exception> criticalImageDecodeFailure;
    private readonly TimeSpan imageDecodeTimeout;
    private int recoveryCompleted;
    private int disposed;

    internal ClientAttachmentDownloadCoordinator(
        AccountScopedLocalCache localCache,
        IClientAttachmentCacheStore cacheStore,
        ClientAttachmentDownloadHttpTransport transport,
        ILogger<ClientAttachmentDownloadCoordinator> logger,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        IWindowsAttachmentShell? attachmentShell = null,
        ClientAttachmentImageDecodeAsync? decodeImageAsync = null,
        TimeSpan? imageDecodeTimeout = null,
        Action<Exception>? criticalImageDecodeFailure = null)
    {
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.attachmentShell = attachmentShell ?? new WindowsAttachmentShell();
        this.decodeImageAsync = decodeImageAsync ?? ClientAttachmentImageDecoder.DecodeAsync;
        this.criticalImageDecodeFailure = criticalImageDecodeFailure ??
            FailFastOnCriticalImageDecodeFailure;
        var imageScopeState = ProcessImageScopeStates.GetOrAdd(
            this.localCache.Identity.DatabasePath,
            static _ => new AttachmentImageScopeState());
        activeImages = imageScopeState.ActiveImages;
        imageProcessingGate = imageScopeState.ProcessingGate;
        this.imageDecodeTimeout = imageDecodeTimeout ?? TimeSpan.FromSeconds(10);
        if (this.imageDecodeTimeout <= TimeSpan.Zero ||
            this.imageDecodeTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(imageDecodeTimeout));
        }
        this.conversationRevokedAsync = conversationRevokedAsync ??
            (static (_, _) => Task.CompletedTask);
        localCache.AttachmentDownloadCancellationRequested += CancelConversationDownloads;
        localCache.AttachmentCachePurged += PurgeConversationCacheAsync;
    }

    public async Task<ClientAttachmentImageLoadOutcome> LoadImageAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentImageRendition rendition,
        ClientAttachmentImageCommit commit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        if (!Enum.IsDefined(rendition))
        {
            throw new ArgumentOutOfRangeException(nameof(rendition));
        }

        ArgumentNullException.ThrowIfNull(commit);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var flightKey = new AttachmentImageFlightKey(
            conversationId,
            attachmentId,
            rendition);
        var flight = new AttachmentImageFlight(coordinatorInstanceId, linkedCancellation);
        if (!activeImages.TryAdd(flightKey, flight))
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.InProgress);
        }

        var token = linkedCancellation.Token;
        using var cancellationBarrier = token.UnsafeRegister(
            static state =>
            {
                var activeImage = (AttachmentImageFlight)state!;
                lock (activeImage.CommitGate)
                {
                }
            },
            flight);
        var enteredProcessingGate = false;
        var imageFlightDetached = false;
        try
        {
            var read = await localCache
                .ReadDownloadedAttachmentAsync(conversationId, attachmentId, token)
                .ConfigureAwait(false);
            if (read.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    MapLocalImageStatus(read.Status));
            }

            if (read.Result != LocalDownloadedAttachmentReadResult.Downloaded ||
                read.Record is null)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    read.Result == LocalDownloadedAttachmentReadResult.AttachmentUnavailable
                        ? ClientAttachmentImageLoadStatus.AttachmentUnavailable
                        : ClientAttachmentImageLoadStatus.NotDownloaded);
            }

            var record = read.Record;
            if (!record.Attachment.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    ClientAttachmentImageLoadStatus.UnsupportedFormat);
            }

            if (record.Attachment.Size > ClientAttachmentImageDecodePolicy.MaximumInputBytes)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    ClientAttachmentImageLoadStatus.SourceTooLarge);
            }

            if (!await imageProcessingGate
                    .WaitAsync(ImageProcessingWaitTimeout, token)
                    .ConfigureAwait(false))
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    ClientAttachmentImageLoadStatus.TimedOut);
            }

            enteredProcessingGate = true;
            AttachmentImageDecodeInput? decodeInput = null;
            Task<ClientAttachmentImageDecodeResult>? decodeTask = null;
            ClientAttachmentImageDecodeResult decoded;
            flight.SetPinsCacheFile(pinsCacheFile: true);
            try
            {
                var resolution = await cacheStore
                    .ValidateAndResolveAsync(
                        record.LocalPath!,
                        CreateKeyFromRecord(record),
                        record.Attachment.Size,
                        token)
                    .ConfigureAwait(false);
                using var file = resolution.File;
                if (resolution.Status != ClientAttachmentCacheStoreStatus.Ready || file is null)
                {
                    return ClientAttachmentImageLoadOutcome.Failure(
                        resolution.Status is ClientAttachmentCacheStoreStatus.NotFound or
                            ClientAttachmentCacheStoreStatus.InvalidRelativePath or
                            ClientAttachmentCacheStoreStatus.ValidationFailed
                            ? ClientAttachmentImageLoadStatus.ValidationFailed
                            : ClientAttachmentImageLoadStatus.LocalCacheFailure);
                }

                decodeInput = await CopyValidatedImageContentAsync(
                        file,
                        record.Attachment.Size,
                        token)
                    .ConfigureAwait(false);
            }
            finally
            {
                flight.SetPinsCacheFile(pinsCacheFile: false);
                await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
            }

            try
            {
                decodeTask = decodeImageAsync(decodeInput.Stream, rendition, token);
                try
                {
                    decoded = await decodeTask
                        .WaitAsync(imageDecodeTimeout, token)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    flight.Cancel();
                    imageFlightDetached = true;
                    enteredProcessingGate = false;
                    _ = ObserveDetachedImageDecodeAsync(
                        flightKey,
                        flight,
                        decodeTask,
                        decodeInput,
                        conversationId);
                    decodeInput = null;
                    return ClientAttachmentImageLoadOutcome.Failure(
                        ClientAttachmentImageLoadStatus.TimedOut);
                }
                catch (OperationCanceledException) when (!decodeTask.IsCompleted)
                {
                    imageFlightDetached = true;
                    enteredProcessingGate = false;
                    _ = ObserveDetachedImageDecodeAsync(
                        flightKey,
                        flight,
                        decodeTask,
                        decodeInput,
                        conversationId);
                    decodeInput = null;
                    throw;
                }
            }
            finally
            {
                if (!imageFlightDetached)
                {
                    decodeInput?.Dispose();
                }
            }

            if (decoded.Status != ClientAttachmentImageDecodeStatus.Success ||
                decoded.Image is null ||
                !decoded.Image.IsFrozen ||
                decoded.SafeSize is null)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    MapDecodeStatus(decoded.Status));
            }

            var committedStatus = ClientAttachmentImageLoadStatus.LocalCacheFailure;
            var confirmation = await localCache
                .ConfirmDownloadedAttachmentAsync(
                    record,
                    () =>
                    {
                        lock (flight.CommitGate)
                        {
                            if (token.IsCancellationRequested)
                            {
                                committedStatus = MapCancellationImageStatus(conversationId);
                                return;
                            }

                            var accessStatus = localCache.GetConversationAccessStatus(
                                conversationId);
                            if (accessStatus != LocalCacheOperationStatus.Ready)
                            {
                                committedStatus = MapLocalImageStatus(accessStatus);
                                return;
                            }

                            committedStatus = commit();
                        }
                    },
                    token)
                .ConfigureAwait(false);
            if (confirmation.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    MapLocalImageStatus(confirmation.Status));
            }

            if (confirmation.Result != LocalDownloadedAttachmentConfirmationResult.Confirmed)
            {
                return ClientAttachmentImageLoadOutcome.Failure(
                    confirmation.Result switch
                    {
                        LocalDownloadedAttachmentConfirmationResult.AttachmentUnavailable =>
                            ClientAttachmentImageLoadStatus.AttachmentUnavailable,
                        LocalDownloadedAttachmentConfirmationResult.NotDownloaded =>
                            ClientAttachmentImageLoadStatus.NotDownloaded,
                        _ => ClientAttachmentImageLoadStatus.Stale,
                    });
            }

            return committedStatus == ClientAttachmentImageLoadStatus.Ready
                ? ClientAttachmentImageLoadOutcome.Ready(decoded)
                : ClientAttachmentImageLoadOutcome.Failure(committedStatus);
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                MapCancellationImageStatus(conversationId));
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.Canceled);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "Loading a downloaded attachment image failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentImageLoadOutcome.Failure(
                ClientAttachmentImageLoadStatus.LocalCacheFailure);
        }
        finally
        {
            if (enteredProcessingGate)
            {
                imageProcessingGate.Release();
            }

            if (!imageFlightDetached &&
                activeImages.TryGetValue(flightKey, out var activeImage) &&
                ReferenceEquals(activeImage, flight))
            {
                activeImages.TryRemove(flightKey, out _);
            }

            await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
        }
    }

    public async Task<ClientAttachmentRevealOutcome> RevealInFolderAsync(
        Guid conversationId,
        Guid attachmentId,
        ClientAttachmentRevealCommit commit,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(attachmentId, nameof(attachmentId));
        ArgumentNullException.ThrowIfNull(commit);
        var flightKey = new AttachmentFlightKey(conversationId, attachmentId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var flight = new AttachmentRevealFlight(linkedCancellation);
        if (!activeReveals.TryAdd(flightKey, flight))
        {
            linkedCancellation.Dispose();
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.Stale);
        }

        var token = linkedCancellation.Token;
        using var cancellationBarrier = token.UnsafeRegister(
            static state =>
            {
                var activeReveal = (AttachmentRevealFlight)state!;
                lock (activeReveal.CommitGate)
                {
                }
            },
            flight);
        try
        {
            var read = await localCache
                .ReadDownloadedAttachmentAsync(conversationId, attachmentId, token)
                .ConfigureAwait(false);
            if (read.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    MapLocalRevealStatus(read.Status));
            }

            if (read.Result != LocalDownloadedAttachmentReadResult.Downloaded ||
                read.Record is null)
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    read.Result == LocalDownloadedAttachmentReadResult.AttachmentUnavailable
                        ? ClientAttachmentRevealStatus.AttachmentUnavailable
                        : ClientAttachmentRevealStatus.NotDownloaded);
            }

            var record = read.Record;
            var resolution = await cacheStore
                .ValidateAndResolveAsync(
                    record.LocalPath!,
                    CreateKeyFromRecord(record),
                    record.Attachment.Size,
                    token)
                .ConfigureAwait(false);
            using var file = resolution.File;
            if (resolution.Status != ClientAttachmentCacheStoreStatus.Ready || file is null)
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    resolution.Status is ClientAttachmentCacheStoreStatus.NotFound or
                        ClientAttachmentCacheStoreStatus.InvalidRelativePath or
                        ClientAttachmentCacheStoreStatus.ValidationFailed
                        ? ClientAttachmentRevealStatus.ValidationFailed
                        : ClientAttachmentRevealStatus.LocalCacheFailure);
            }

            var committedStatus = ClientAttachmentRevealStatus.LocalCacheFailure;
            var confirmation = await localCache
                .ConfirmDownloadedAttachmentAsync(
                    record,
                    () =>
                    {
                        lock (flight.CommitGate)
                        {
                            if (token.IsCancellationRequested)
                            {
                                committedStatus = MapCancellationRevealStatus(conversationId);
                                return;
                            }

                            var accessStatus = localCache.GetConversationAccessStatus(
                                conversationId);
                            if (accessStatus != LocalCacheOperationStatus.Ready)
                            {
                                committedStatus = MapLocalRevealStatus(accessStatus);
                                return;
                            }

                            // This is the final, one-way reveal-start transition.
                            // It runs under the account and cache linearization
                            // locks, but it does not call the native Shell. Explorer
                            // is allowed to block indefinitely, so it must run only
                            // after this callback, the SQLite transaction, and all
                            // coordinator gates have been released.
                            committedStatus = commit();
                        }
                    },
                    token)
                .ConfigureAwait(false);
            if (confirmation.Status != LocalCacheOperationStatus.Ready)
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    MapLocalRevealStatus(confirmation.Status));
            }

            if (confirmation.Result != LocalDownloadedAttachmentConfirmationResult.Confirmed)
            {
                return ClientAttachmentRevealOutcome.FromStatus(
                    confirmation.Result switch
                    {
                        LocalDownloadedAttachmentConfirmationResult.AttachmentUnavailable =>
                            ClientAttachmentRevealStatus.AttachmentUnavailable,
                        LocalDownloadedAttachmentConfirmationResult.NotDownloaded =>
                            ClientAttachmentRevealStatus.NotDownloaded,
                        _ => ClientAttachmentRevealStatus.Stale,
                    });
            }

            if (committedStatus != ClientAttachmentRevealStatus.Revealed)
            {
                return ClientAttachmentRevealOutcome.FromStatus(committedStatus);
            }

            // A successful commit is the reveal's linearization point. Later
            // cancellation, selection changes, or revocation are deliberately
            // ordered after Shell start and cannot suppress this already-authorized
            // native call. The validated capability remains pinned until it returns.
            return ClientAttachmentRevealOutcome.FromStatus(
                attachmentShell.Reveal(file) == WindowsAttachmentShellStatus.Revealed
                    ? ClientAttachmentRevealStatus.Revealed
                    : ClientAttachmentRevealStatus.ShellUnavailable);
        }
        catch (OperationCanceledException)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                MapCancellationRevealStatus(conversationId));
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.Canceled);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Revealing a downloaded attachment failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.LocalCacheFailure);
        }
        finally
        {
            if (activeReveals.TryGetValue(flightKey, out var activeReveal) &&
                ReferenceEquals(activeReveal, flight))
            {
                activeReveals.TryRemove(flightKey, out _);
            }

            await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
        }
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

            foreach (var reveal in activeReveals.Values)
            {
                try
                {
                    reveal.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A completed reveal may dispose concurrently with runtime shutdown.
                }
            }

            foreach (var image in activeImages.Values)
            {
                if (image.OwnerId != coordinatorInstanceId)
                {
                    continue;
                }

                try
                {
                    image.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A completed image flight may dispose concurrently with shutdown.
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


        foreach (var reveal in activeReveals)
        {
            if (reveal.Key.ConversationId != conversationId)
            {
                continue;
            }

            try
            {
                reveal.Value.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion may race a durable revocation notification.
            }
        }

        foreach (var image in activeImages)
        {
            if (image.Key.ConversationId != conversationId)
            {
                continue;
            }

            try
            {
                image.Value.Cancellation.Cancel();
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
            activeFlights.Keys.Any(key => key.ConversationId == conversationId) ||
            activeReveals.Keys.Any(key => key.ConversationId == conversationId) ||
            activeImages.Any(image =>
                image.Key.ConversationId == conversationId &&
                image.Value.PinsCacheFile))
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

    private static async Task<AttachmentImageDecodeInput> CopyValidatedImageContentAsync(
        ClientAttachmentCacheStore.ValidatedFile file,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (expectedLength is < 1 or > ClientAttachmentImageDecodePolicy.MaximumInputBytes)
        {
            throw new InvalidDataException(
                "The validated image length is outside the preview input budget.");
        }

        return await file
            .ReadContentAsync(
                async (content, readCancellation) =>
                {
                    if (content.Length != expectedLength)
                    {
                        throw new InvalidDataException(
                            "The validated image content length changed unexpectedly.");
                    }

                    var bytes = GC.AllocateUninitializedArray<byte>(checked((int)expectedLength));
                    try
                    {
                        await content
                            .ReadExactlyAsync(bytes.AsMemory(), readCancellation)
                            .ConfigureAwait(false);
                        if (content.ReadByte() != -1)
                        {
                            throw new InvalidDataException(
                                "The validated image content exceeded its expected length.");
                        }

                        return new AttachmentImageDecodeInput(bytes);
                    }
                    catch
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                        throw;
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ObserveDetachedImageDecodeAsync(
        AttachmentImageFlightKey flightKey,
        AttachmentImageFlight flight,
        Task<ClientAttachmentImageDecodeResult> decodeTask,
        AttachmentImageDecodeInput decodeInput,
        Guid conversationId)
    {
        Exception? criticalFailure = null;
        try
        {
            _ = await decodeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(
                "A detached attachment image decoder completed with an error; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
        catch (Exception exception)
        {
            criticalFailure = exception;
        }
        finally
        {
            decodeInput.Dispose();
            imageProcessingGate.Release();
            if (activeImages.TryGetValue(flightKey, out var activeImage) &&
                ReferenceEquals(activeImage, flight))
            {
                activeImages.TryRemove(flightKey, out _);
            }

            await RetryPendingPurgeIfQuiescentAsync(conversationId).ConfigureAwait(false);
        }

        if (criticalFailure is not null)
        {
            criticalImageDecodeFailure(criticalFailure);
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

    private ClientAttachmentRevealStatus MapCancellationRevealStatus(Guid conversationId)
    {
        try
        {
            return localCache.GetConversationAccessStatus(conversationId) ==
                LocalCacheOperationStatus.RevokedConversation
                    ? ClientAttachmentRevealStatus.AccessRevoked
                    : ClientAttachmentRevealStatus.Canceled;
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentRevealStatus.Canceled;
        }
    }

    private ClientAttachmentImageLoadStatus MapCancellationImageStatus(Guid conversationId)
    {
        try
        {
            return localCache.GetConversationAccessStatus(conversationId) ==
                LocalCacheOperationStatus.RevokedConversation
                    ? ClientAttachmentImageLoadStatus.AccessRevoked
                    : ClientAttachmentImageLoadStatus.Canceled;
        }
        catch (ObjectDisposedException)
        {
            return ClientAttachmentImageLoadStatus.Canceled;
        }
    }

    private static ClientAttachmentImageLoadStatus MapLocalImageStatus(
        LocalCacheOperationStatus status) =>
        status switch
        {
            LocalCacheOperationStatus.RevokedConversation =>
                ClientAttachmentImageLoadStatus.AccessRevoked,
            LocalCacheOperationStatus.UnknownConversation =>
                ClientAttachmentImageLoadStatus.AttachmentUnavailable,
            LocalCacheOperationStatus.TransientFailure =>
                ClientAttachmentImageLoadStatus.TransientFailure,
            LocalCacheOperationStatus.Conflict => ClientAttachmentImageLoadStatus.Stale,
            _ => ClientAttachmentImageLoadStatus.LocalCacheFailure,
        };

    private static ClientAttachmentImageLoadStatus MapDecodeStatus(
        ClientAttachmentImageDecodeStatus status) =>
        status switch
        {
            ClientAttachmentImageDecodeStatus.InvalidInput =>
                ClientAttachmentImageLoadStatus.ValidationFailed,
            ClientAttachmentImageDecodeStatus.UnsupportedFormat or
                ClientAttachmentImageDecodeStatus.UnsupportedCodec =>
                ClientAttachmentImageLoadStatus.UnsupportedFormat,
            ClientAttachmentImageDecodeStatus.SourceTooLarge =>
                ClientAttachmentImageLoadStatus.SourceTooLarge,
            ClientAttachmentImageDecodeStatus.OutputTooLarge =>
                ClientAttachmentImageLoadStatus.OutputTooLarge,
            _ => ClientAttachmentImageLoadStatus.DecodeFailed,
        };

    private static ClientAttachmentRevealStatus MapLocalRevealStatus(
        LocalCacheOperationStatus status) =>
        status switch
        {
            LocalCacheOperationStatus.RevokedConversation =>
                ClientAttachmentRevealStatus.AccessRevoked,
            LocalCacheOperationStatus.UnknownConversation =>
                ClientAttachmentRevealStatus.AttachmentUnavailable,
            LocalCacheOperationStatus.TransientFailure =>
                ClientAttachmentRevealStatus.TransientFailure,
            LocalCacheOperationStatus.Conflict =>
                ClientAttachmentRevealStatus.Stale,
            _ => ClientAttachmentRevealStatus.LocalCacheFailure,
        };

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

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static void FailFastOnCriticalImageDecodeFailure(Exception exception) =>
        Environment.FailFast(
            "A detached attachment image decoder encountered a critical process failure.",
            exception);

    private readonly record struct AttachmentFlightKey(
        Guid ConversationId,
        Guid AttachmentId);

    private readonly record struct AttachmentImageFlightKey(
        Guid ConversationId,
        Guid AttachmentId,
        ClientAttachmentImageRendition Rendition);

    private sealed class AttachmentRevealFlight(
        CancellationTokenSource cancellation)
    {
        public object CommitGate { get; } = new();

        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    private sealed class AttachmentImageScopeState
    {
        public ConcurrentDictionary<AttachmentImageFlightKey, AttachmentImageFlight>
            ActiveImages
        { get; } = new();

        public SemaphoreSlim ProcessingGate { get; } = new(2, 2);
    }

    private sealed class AttachmentImageFlight(
        Guid ownerId,
        CancellationTokenSource cancellation)
    {
        private int pinsCacheFile;

        public Guid OwnerId { get; } = ownerId;

        public object CommitGate { get; } = new();

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool PinsCacheFile => Volatile.Read(ref pinsCacheFile) != 0;

        public void SetPinsCacheFile(bool pinsCacheFile) =>
            Volatile.Write(ref this.pinsCacheFile, pinsCacheFile ? 1 : 0);

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class AttachmentImageDecodeInput : IDisposable
    {
        private byte[]? bytes;

        internal AttachmentImageDecodeInput(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            this.bytes = bytes;
            Stream = new MemoryStream(
                bytes,
                index: 0,
                count: bytes.Length,
                writable: false,
                publiclyVisible: false);
        }

        internal MemoryStream Stream { get; }

        public void Dispose()
        {
            Stream.Dispose();
            var ownedBytes = Interlocked.Exchange(ref bytes, null);
            if (ownedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(ownedBytes);
            }
        }

        public override string ToString() =>
            $"{nameof(AttachmentImageDecodeInput)} {{ Content = [REDACTED] }}";
    }
}
