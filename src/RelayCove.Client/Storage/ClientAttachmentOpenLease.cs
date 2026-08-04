namespace RelayCove.Client.Storage;

internal sealed class ClientAttachmentOpenLease : IAsyncDisposable
{
    private readonly ClientAttachmentOpenStore owner;
    private readonly string localPath;
    private int state;
    private int purgeRequested;

    internal ClientAttachmentOpenLease(ClientAttachmentOpenStore owner, string localPath)
    {
        this.owner = owner;
        this.localPath = localPath;
    }

    // This capability is only for the Windows Attachment Manager boundary. It must never
    // be projected through a result, presentation, log, or automation property.
    internal string LocalPath
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return localPath;
        }
    }

    internal bool IsOwnedBy(ClientAttachmentOpenStore store) => ReferenceEquals(owner, store);

    internal bool IsCommitted => Volatile.Read(ref state) is StateCommitted or StateLaunchCompleted;

    internal bool IsDisposed => Volatile.Read(ref state) == StateDisposed;

    internal bool IsLaunchCompleted => Volatile.Read(ref state) == StateLaunchCompleted;

    internal bool IsPurgeRequested => Volatile.Read(ref purgeRequested) != 0;

    // This is deliberately synchronous and does no I/O. It is called from the coordinator's
    // final authorization/UI commit beside the already accepted STA work item.
    internal void Commit() => owner.Commit(this);

    internal bool TryMarkCommitted() =>
        Interlocked.CompareExchange(ref state, StateCommitted, StateActive) == StateActive ||
        Volatile.Read(ref state) is StateCommitted or StateLaunchCompleted;

    internal bool TryMarkLaunchCompleted() =>
        Interlocked.CompareExchange(ref state, StateLaunchCompleted, StateCommitted) == StateCommitted ||
        Volatile.Read(ref state) == StateLaunchCompleted;

    internal void RequestPurge() => Interlocked.Exchange(ref purgeRequested, 1);

    internal bool TryDisposePrecommit() =>
        Interlocked.CompareExchange(ref state, StateDisposed, StateActive) == StateActive;

    internal void MarkDisposedAfterCleanup() => Interlocked.Exchange(ref state, StateDisposed);

    public ValueTask DisposeAsync() => new(owner.DisposeLeaseAsync(this));

    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenLease)} {{ LocalPath = [REDACTED] }}";

    private const int StateActive = 0;
    private const int StateCommitted = 1;
    private const int StateLaunchCompleted = 2;
    private const int StateDisposed = 3;
}
