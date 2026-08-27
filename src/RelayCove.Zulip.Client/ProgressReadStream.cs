using RelayCove.Core;

namespace RelayCove.Zulip.Client;

internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly IProgress<RealmMediaTransferProgress> _progress;
    private long _transferred;

    public ProgressReadStream(
        Stream inner,
        long length,
        IProgress<RealmMediaTransferProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(progress);
        if (!inner.CanRead) throw new ArgumentException("The upload stream must be readable.", nameof(inner));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _inner = inner;
        _length = length;
        _progress = progress;
        _progress.Report(new RealmMediaTransferProgress(0, length));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Report(int bytesRead)
    {
        if (bytesRead <= 0) return;
        _transferred = Math.Min(_length, _transferred + bytesRead);
        _progress.Report(new RealmMediaTransferProgress(_transferred, _length));
    }
}
