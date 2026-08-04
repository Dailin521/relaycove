using System.IO;

namespace RelayCove.Client.Storage;

internal sealed class ClientAttachmentCacheStoreStagingFile : IAsyncDisposable
{
    private readonly ClientAttachmentCacheStore owner;
    private readonly string path;
    private FileStream? stream;
    private int completed;

    internal ClientAttachmentCacheStoreStagingFile(
        ClientAttachmentCacheStore owner,
        Guid conversationId,
        Guid attachmentId,
        long expectedSize,
        string path,
        FileStream stream)
    {
        this.owner = owner;
        ConversationId = conversationId;
        AttachmentId = attachmentId;
        ExpectedSize = expectedSize;
        this.path = path;
        this.stream = stream;
    }

    internal Guid ConversationId { get; }

    internal Guid AttachmentId { get; }

    internal long ExpectedSize { get; }

    internal Stream Stream => stream ?? throw new ObjectDisposedException(nameof(ClientAttachmentCacheStoreStagingFile));

    internal bool IsOwnedBy(ClientAttachmentCacheStore store) => ReferenceEquals(owner, store);

    internal string TakePathForPublish()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            throw new InvalidOperationException("The staging file has already been completed.");
        }

        return path;
    }

    internal async Task FlushAndCloseAsync(CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref stream, null);
        if (current is null)
        {
            throw new InvalidOperationException("The staging stream has already been closed.");
        }

        await using (current.ConfigureAwait(false))
        {
            await current.FlushAsync(cancellationToken).ConfigureAwait(false);
            current.Flush(flushToDisk: true);
        }
    }

    internal async Task<string?> TakePathForDiscardAsync()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return path;
        }

        var current = Interlocked.Exchange(ref stream, null);
        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }

        return path;
    }

    public ValueTask DisposeAsync() => new(owner.DiscardAsync(this));

    public override string ToString() =>
        $"{nameof(ClientAttachmentCacheStoreStagingFile)} {{ ConversationId = [REDACTED], " +
        "AttachmentId = [REDACTED], ExpectedSize = [REDACTED], Stream = [REDACTED] }";
}
