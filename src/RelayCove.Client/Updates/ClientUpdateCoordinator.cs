using Microsoft.Extensions.Logging;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed class ClientUpdateCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IClientUpdateManifestTransport manifestTransport;
    private readonly IClientCurrentVersionProvider currentVersionProvider;
    private readonly IClientUpdateDownloader downloader;
    private readonly ILogger logger;
    private ClientUpdateState state = ClientUpdateState.Idle;
    private CheckFlight? activeCheck;
    private DownloadFlight? activeDownload;
    private long generation;
    private int disposed;

    public ClientUpdateCoordinator(
        IClientUpdateManifestTransport manifestTransport,
        IClientCurrentVersionProvider currentVersionProvider,
        IClientUpdateDownloader downloader,
        ILogger logger)
    {
        this.manifestTransport = manifestTransport ?? throw new ArgumentNullException(nameof(manifestTransport));
        this.currentVersionProvider = currentVersionProvider ??
            throw new ArgumentNullException(nameof(currentVersionProvider));
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<ClientUpdateState>? StateChanged;

    public ClientUpdateState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public Task<ClientUpdateState> CheckAsync(
        Uri serverBaseUri,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedBaseUri = ClientUpdateServerUri.Canonicalize(serverBaseUri);
        CheckFlight flight;
        lock (gate)
        {
            if (activeCheck is { } existing && Equals(existing.ServerBaseUri, normalizedBaseUri))
            {
                flight = existing;
            }
            else
            {
                activeCheck?.Cancellation.Cancel();
                if (activeDownload is not null)
                {
                    activeDownload.Cancellation.Cancel();
                    downloader.Cancel();
                }

                var nextGeneration = ++generation;
                flight = new CheckFlight(normalizedBaseUri, nextGeneration);
                activeCheck = flight;
                PublishLocked(new ClientUpdateState(
                    ClientUpdatePhase.Checking,
                    CurrentVersion: null,
                    Manifest: null,
                    Decision: null,
                    Progress: null,
                    ArchivePath: null,
                    ClientUpdateFailure.None));
                flight.Task = CheckCoreAsync(flight);
            }
        }

        return cancellationToken.CanBeCanceled
            ? flight.Task!.WaitAsync(cancellationToken)
            : flight.Task!;
    }

    public Task<ClientUpdateState> DownloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DownloadFlight flight;
        lock (gate)
        {
            if (activeDownload is { } existing)
            {
                flight = existing;
            }
            else
            {
                if (state.Manifest is null || state.Decision is not (
                    UpdateDecisionKind.Optional or UpdateDecisionKind.Mandatory or UpdateDecisionKind.Unsupported))
                {
                    PublishLocked(state with
                    {
                        Phase = ClientUpdatePhase.Failed,
                        Failure = ClientUpdateFailure.NoUpdateAvailable,
                        Progress = null,
                        ArchivePath = null,
                    });
                    return Task.FromResult(state);
                }

                flight = new DownloadFlight(state.Manifest, generation);
                activeDownload = flight;
                PublishLocked(state with
                {
                    Phase = ClientUpdatePhase.Downloading,
                    Progress = new ClientUpdateDownloadProgress(0, state.Manifest.Artifact.SizeBytes),
                    ArchivePath = null,
                    Failure = ClientUpdateFailure.None,
                });
                flight.Task = DownloadCoreAsync(flight);
            }
        }

        return cancellationToken.CanBeCanceled
            ? flight.Task!.WaitAsync(cancellationToken)
            : flight.Task!;
    }

    public void CancelCheck()
    {
        lock (gate)
        {
            activeCheck?.Cancellation.Cancel();
        }
    }

    public void CancelDownload()
    {
        lock (gate)
        {
            activeDownload?.Cancellation.Cancel();
            downloader.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lock (gate)
            {
                activeCheck?.Cancellation.Cancel();
                activeDownload?.Cancellation.Cancel();
                downloader.Cancel();
                generation++;
            }
        }

        return ValueTask.CompletedTask;
    }

    public override string ToString() =>
        $"{nameof(ClientUpdateCoordinator)} {{ State = {State} }}";

    private async Task<ClientUpdateState> CheckCoreAsync(CheckFlight flight)
    {
        ClientUpdateState next;
        try
        {
            var currentVersion = currentVersionProvider.GetCurrentVersion();
            if (!SemanticVersion.TryParse(currentVersion, out _))
            {
                next = Failed(ClientUpdateFailure.CurrentVersionInvalid, currentVersion: null);
            }
            else
            {
                var fetched = await manifestTransport.FetchAsync(flight.ServerBaseUri, flight.Cancellation.Token)
                    .ConfigureAwait(false);
                next = ToCheckedState(currentVersion, fetched);
            }
        }
        catch (OperationCanceledException) when (flight.Cancellation.IsCancellationRequested)
        {
            next = Failed(ClientUpdateFailure.Canceled, currentVersion: null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            logger.LogWarning(
                "Update check failed; errorType={ErrorType}.",
                exception.GetType().Name);
            next = Failed(ClientUpdateFailure.ManifestUnavailable, currentVersion: null);
        }
        lock (gate)
        {
            if (ReferenceEquals(activeCheck, flight))
            {
                activeCheck = null;
            }

            flight.Cancellation.Dispose();

            if (generation == flight.Generation && Volatile.Read(ref disposed) == 0)
            {
                PublishLocked(next);
                return state;
            }

            return state;
        }
    }

    private async Task<ClientUpdateState> DownloadCoreAsync(DownloadFlight flight)
    {
        ClientUpdateDownloadOutcome outcome;
        try
        {
            outcome = await downloader.DownloadAsync(
                    flight.Manifest,
                    progress => PublishDownloadProgress(flight, progress),
                    flight.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flight.Cancellation.IsCancellationRequested)
        {
            outcome = ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.Canceled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            logger.LogWarning(
                "Update package download coordination failed; errorType={ErrorType}.",
                exception.GetType().Name);
            outcome = ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.TransientFailure);
        }
        lock (gate)
        {
            if (ReferenceEquals(activeDownload, flight))
            {
                activeDownload = null;
            }

            flight.Cancellation.Dispose();

            if (generation != flight.Generation || Volatile.Read(ref disposed) != 0)
            {
                return state;
            }

            if (outcome.Status == ClientUpdateDownloadStatus.Success && outcome.ArchivePath is not null)
            {
                PublishLocked(state with
                {
                    Phase = ClientUpdatePhase.Downloaded,
                    Progress = new ClientUpdateDownloadProgress(
                        flight.Manifest.Artifact.SizeBytes,
                        flight.Manifest.Artifact.SizeBytes),
                    ArchivePath = outcome.ArchivePath,
                    Failure = ClientUpdateFailure.None,
                });
            }
            else
            {
                PublishLocked(state with
                {
                    Phase = ClientUpdatePhase.Failed,
                    Progress = null,
                    ArchivePath = null,
                    Failure = outcome.Status == ClientUpdateDownloadStatus.Canceled
                        ? ClientUpdateFailure.Canceled
                        : ClientUpdateFailure.DownloadFailed,
                });
            }

            return state;
        }
    }

    private ClientUpdateState ToCheckedState(
        string currentVersion,
        ClientUpdateManifestFetchOutcome fetched)
    {
        if (fetched.Status != ClientUpdateFetchStatus.Success || fetched.Manifest is null)
        {
            return Failed(
                fetched.Status == ClientUpdateFetchStatus.Canceled
                    ? ClientUpdateFailure.Canceled
                    : fetched.Status == ClientUpdateFetchStatus.ProtocolError
                        ? ClientUpdateFailure.ManifestInvalid
                        : ClientUpdateFailure.ManifestUnavailable,
                currentVersion);
        }

        try
        {
            var decision = UpdateDecisionEvaluator.Evaluate(currentVersion, fetched.Manifest);
            return new ClientUpdateState(
                decision switch
                {
                    UpdateDecisionKind.None => ClientUpdatePhase.NoUpdate,
                    UpdateDecisionKind.Optional => ClientUpdatePhase.OptionalAvailable,
                    UpdateDecisionKind.Mandatory or UpdateDecisionKind.Unsupported =>
                        ClientUpdatePhase.MandatoryAvailable,
                    _ => throw new InvalidOperationException("The update decision is unsupported."),
                },
                currentVersion,
                fetched.Manifest,
                decision,
                Progress: null,
                ArchivePath: null,
                ClientUpdateFailure.None);
        }
        catch (ArgumentException)
        {
            return Failed(ClientUpdateFailure.ManifestInvalid, currentVersion);
        }
    }

    private static ClientUpdateState Failed(ClientUpdateFailure failure, string? currentVersion) =>
        new(
            ClientUpdatePhase.Failed,
            currentVersion,
            Manifest: null,
            Decision: null,
            Progress: null,
            ArchivePath: null,
            failure);

    private void PublishDownloadProgress(DownloadFlight flight, ClientUpdateDownloadProgress progress)
    {
        lock (gate)
        {
            if (ReferenceEquals(activeDownload, flight) && generation == flight.Generation &&
                Volatile.Read(ref disposed) == 0)
            {
                PublishLocked(state with { Progress = progress });
            }
        }
    }

    private void PublishLocked(ClientUpdateState next)
    {
        state = next;
        var listeners = StateChanged;
        if (listeners is null)
        {
            return;
        }

        foreach (Action<ClientUpdateState> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(next);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                logger.LogWarning(
                    "Update state listener failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ClientUpdateCoordinator));
        }
    }

    private sealed class CheckFlight(Uri serverBaseUri, long generation)
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public long Generation { get; } = generation;

        public Uri ServerBaseUri { get; } = serverBaseUri;

        public Task<ClientUpdateState>? Task { get; set; }
    }

    private sealed class DownloadFlight(UpdateManifestDto manifest, long generation)
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public long Generation { get; } = generation;

        public UpdateManifestDto Manifest { get; } = manifest;

        public Task<ClientUpdateState>? Task { get; set; }
    }
}
