using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal sealed class ClientReadThroughCoordinator : IClientAccountReadThroughCoordinator
{
    private const int BatchSize = 100;
    private readonly object stateGate = new();
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientReadThroughHttpTransport transport;
    private readonly ILogger<ClientReadThroughCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly Dictionary<Guid, long> acknowledgedTargets = new();
    private readonly HashSet<Guid> suppressedConversations = new();
    private Task<ClientReadThroughRunOutcome>? activeFlight;
    private long deferredSnapshotRevision = -1;
    private long observedSnapshotRevision = -1;
    private bool rerunRequested;
    private bool rerunActive;
    private int disposed;

    public ClientReadThroughCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientReadThroughCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!string.Equals(identity.Id, localCache.Identity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The local cache must belong to the read-through coordinator account scope.",
                nameof(localCache));
        }

        transport = new ClientReadThroughHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger);
    }

    public Task<ClientReadThroughRunOutcome> TriggerAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<ClientReadThroughRunOutcome>? newFlight = null;
        Task<ClientReadThroughRunOutcome> flight;
        lock (stateGate)
        {
            ThrowIfDisposed();
            if (activeFlight is null)
            {
                rerunRequested = false;
                rerunActive = false;
                newFlight = new TaskCompletionSource<ClientReadThroughRunOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                activeFlight = newFlight.Task;
            }
            else if (!rerunActive)
            {
                rerunRequested = true;
            }

            flight = activeFlight;
        }

        if (newFlight is not null)
        {
            _ = ExecuteFlightAsync(newFlight);
        }

        return cancellationToken.CanBeCanceled
            ? flight.WaitAsync(cancellationToken)
            : flight;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        Task<ClientReadThroughRunOutcome>? flight;
        lock (stateGate)
        {
            flight = activeFlight;
        }

        if (flight is not null)
        {
            await flight.ConfigureAwait(false);
        }

        lifetimeCancellation.Dispose();
    }

    private async Task ExecuteFlightAsync(
        TaskCompletionSource<ClientReadThroughRunOutcome> completion)
    {
        var requestCount = 0;
        var receiptCount = 0;
        var finalStatus = ClientReadThroughRunStatus.Canceled;
        try
        {
            var firstPass = await RunPassAsync(lifetimeCancellation.Token)
                .ConfigureAwait(false);
            finalStatus = firstPass.Status;
            requestCount += firstPass.RequestCount;
            receiptCount += firstPass.ReceiptCount;

            var runAgain = false;
            lock (stateGate)
            {
                if (CanRunBoundedRerun(finalStatus) &&
                    rerunRequested &&
                    !lifetimeCancellation.IsCancellationRequested)
                {
                    rerunRequested = false;
                    rerunActive = true;
                    runAgain = true;
                }
            }

            if (runAgain)
            {
                var secondPass = await RunPassAsync(lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                finalStatus = CanRunBoundedRerun(secondPass.Status)
                    ? SelectMoreSevere(finalStatus, secondPass.Status)
                    : secondPass.Status;
                requestCount += secondPass.RequestCount;
                receiptCount += secondPass.ReceiptCount;
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            finalStatus = ClientReadThroughRunStatus.Canceled;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Read-through upload flight failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            finalStatus = ClientReadThroughRunStatus.LocalCacheFailure;
        }

        lock (stateGate)
        {
            rerunRequested = false;
            rerunActive = false;
            activeFlight = null;
            completion.TrySetResult(new ClientReadThroughRunOutcome(
                finalStatus,
                requestCount,
                receiptCount));
        }
    }

    private async Task<ClientReadThroughRunOutcome> RunPassAsync(
        CancellationToken cancellationToken)
    {
        var requestCount = 0;
        var receiptCount = 0;
        var finalStatus = ClientReadThroughRunStatus.Completed;
        if (deferredSnapshotRevision == localCache.AuthoritativeSnapshotRevision)
        {
            return new ClientReadThroughRunOutcome(finalStatus, requestCount, receiptCount);
        }

        Guid? afterConversationId = null;
        while (true)
        {
            var batch = await localCache
                .ReadPendingReadThroughBatchAsync(
                    afterConversationId,
                    BatchSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (batch.Status != LocalCacheOperationStatus.Ready)
            {
                if (batch.Status == LocalCacheOperationStatus.TransientFailure)
                {
                    AlignTargetState(batch.SnapshotRevision);
                    deferredSnapshotRevision = batch.SnapshotRevision;
                }

                return new ClientReadThroughRunOutcome(
                    batch.Status == LocalCacheOperationStatus.TransientFailure
                        ? ClientReadThroughRunStatus.TransientFailure
                        : ClientReadThroughRunStatus.LocalCacheFailure,
                    requestCount,
                    receiptCount);
            }

            AlignTargetState(batch.SnapshotRevision);

            foreach (var target in batch.Targets)
            {
                if (acknowledgedTargets.TryGetValue(target.ConversationId, out var acknowledged) &&
                    acknowledged >= target.SafeMessageId)
                {
                    continue;
                }

                if (suppressedConversations.Contains(target.ConversationId))
                {
                    continue;
                }

                requestCount++;
                var result = await transport
                    .MarkReadAsync(
                        target.ConversationId,
                        target.SafeMessageId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status != ClientReadThroughHttpStatus.Success)
                {
                    if (result.Status == ClientReadThroughHttpStatus.AccessRevoked)
                    {
                        var revokeStatus = await localCache
                            .RevokeConversationAccessAsync(
                                target.ConversationId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        acknowledgedTargets.Remove(target.ConversationId);
                        suppressedConversations.Remove(target.ConversationId);
                        if (revokeStatus == LocalCacheOperationStatus.RevokedConversation)
                        {
                            continue;
                        }

                        return new ClientReadThroughRunOutcome(
                            ClientReadThroughRunStatus.LocalCacheFailure,
                            requestCount,
                            receiptCount);
                    }

                    var mappedStatus = MapHttpStatus(result.Status);
                    if (mappedStatus is ClientReadThroughRunStatus.AuthenticationRequired or
                        ClientReadThroughRunStatus.TransientFailure)
                    {
                        deferredSnapshotRevision = observedSnapshotRevision;
                        return new ClientReadThroughRunOutcome(
                            mappedStatus,
                            requestCount,
                            receiptCount);
                    }

                    suppressedConversations.Add(target.ConversationId);
                    finalStatus = SelectMoreSevere(finalStatus, mappedStatus);
                    continue;
                }

                var receipt = result.Receipt!;
                if (receipt.ConversationId != target.ConversationId ||
                    receipt.LastReadMessageId < target.SafeMessageId)
                {
                    suppressedConversations.Add(target.ConversationId);
                    finalStatus = SelectMoreSevere(
                        finalStatus,
                        ClientReadThroughRunStatus.ProtocolError);
                    continue;
                }

                var applyStatus = await localCache
                    .ApplyReadThroughReceiptAsync(
                        target.ConversationId,
                        target.SafeMessageId,
                        receipt.LastReadMessageId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (applyStatus is LocalCacheOperationStatus.RevokedConversation or
                    LocalCacheOperationStatus.UnknownConversation)
                {
                    acknowledgedTargets.Remove(target.ConversationId);
                    suppressedConversations.Remove(target.ConversationId);
                    continue;
                }

                if (applyStatus != LocalCacheOperationStatus.Ready)
                {
                    return new ClientReadThroughRunOutcome(
                        ClientReadThroughRunStatus.LocalCacheFailure,
                        requestCount,
                        receiptCount);
                }

                acknowledgedTargets[target.ConversationId] = receipt.LastReadMessageId;
                suppressedConversations.Remove(target.ConversationId);
                receiptCount++;
            }

            if (batch.ContinuationConversationId is not { } continuation)
            {
                break;
            }

            if (continuation == afterConversationId)
            {
                return new ClientReadThroughRunOutcome(
                    ClientReadThroughRunStatus.LocalCacheFailure,
                    requestCount,
                    receiptCount);
            }

            afterConversationId = continuation;
        }

        return new ClientReadThroughRunOutcome(finalStatus, requestCount, receiptCount);
    }

    private static ClientReadThroughRunStatus MapHttpStatus(
        ClientReadThroughHttpStatus status) =>
        status switch
        {
            ClientReadThroughHttpStatus.AuthenticationRequired =>
                ClientReadThroughRunStatus.AuthenticationRequired,
            ClientReadThroughHttpStatus.TransientFailure =>
                ClientReadThroughRunStatus.TransientFailure,
            ClientReadThroughHttpStatus.ProtocolError =>
                ClientReadThroughRunStatus.ProtocolError,
            ClientReadThroughHttpStatus.AccessDenied =>
                ClientReadThroughRunStatus.AccessDenied,
            ClientReadThroughHttpStatus.RemoteFailure =>
                ClientReadThroughRunStatus.RemoteFailure,
            _ => throw new InvalidOperationException("Unexpected read-through HTTP status."),
        };

    private void AlignTargetState(long snapshotRevision)
    {
        if (snapshotRevision == observedSnapshotRevision)
        {
            return;
        }

        acknowledgedTargets.Clear();
        suppressedConversations.Clear();
        deferredSnapshotRevision = -1;
        observedSnapshotRevision = snapshotRevision;
    }

    private static bool CanRunBoundedRerun(ClientReadThroughRunStatus status) =>
        status is ClientReadThroughRunStatus.Completed or
            ClientReadThroughRunStatus.ProtocolError or
            ClientReadThroughRunStatus.AccessDenied or
            ClientReadThroughRunStatus.RemoteFailure;

    private static ClientReadThroughRunStatus SelectMoreSevere(
        ClientReadThroughRunStatus current,
        ClientReadThroughRunStatus candidate)
    {
        if (current == ClientReadThroughRunStatus.Completed)
        {
            return candidate;
        }

        static int Rank(ClientReadThroughRunStatus status) =>
            status switch
            {
                ClientReadThroughRunStatus.ProtocolError => 3,
                ClientReadThroughRunStatus.AccessDenied => 2,
                ClientReadThroughRunStatus.RemoteFailure => 1,
                _ => 0,
            };

        return Rank(candidate) > Rank(current) ? candidate : current;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
