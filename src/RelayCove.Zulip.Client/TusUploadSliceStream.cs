using RelayCove.Core;

namespace RelayCove.Zulip.Client;

internal sealed class TusUploadSliceStream : Stream
{
    private readonly Stream _inner;
    private readonly long _sliceLength;
    private readonly long _uploadOffset;
    private readonly long _totalLength;
    private readonly IProgress<RealmMediaTransferProgress>? _progress;
    private long _consumed;

    public TusUploadSliceStream(
        Stream inner,
        long sourcePosition,
        long sliceLength,
        long uploadOffset,
        long totalLength,
        IProgress<RealmMediaTransferProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek)
            throw new ArgumentException("A readable, seekable stream is required for resumable upload.", nameof(inner));
        if (sourcePosition < 0 || sliceLength <= 0 || uploadOffset < 0 || totalLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceLength));
        _inner = inner;
        _sliceLength = sliceLength;
        _uploadOffset = uploadOffset;
        _totalLength = totalLength;
        _progress = progress;
        _inner.Position = sourcePosition;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _sliceLength;
    public override long Position
    {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_consumed >= _sliceLength) return 0;
        var requested = (int)Math.Min(buffer.Length, _sliceLength - _consumed);
        var read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        return CompleteRead(read);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (_consumed >= _sliceLength) return 0;
        var requested = (int)Math.Min(count, _sliceLength - _consumed);
        var read = await _inner.ReadAsync(buffer, offset, requested, cancellationToken).ConfigureAwait(false);
        return CompleteRead(read);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int ReadCore(Span<byte> buffer)
    {
        if (_consumed >= _sliceLength) return 0;
        var requested = (int)Math.Min(buffer.Length, _sliceLength - _consumed);
        return CompleteRead(_inner.Read(buffer[..requested]));
    }

    private int CompleteRead(int read)
    {
        if (read == 0 && _consumed < _sliceLength)
            throw new EndOfStreamException("The upload stream ended before the declared file length.");
        _consumed += read;
        if (read > 0)
        {
            _progress?.Report(new RealmMediaTransferProgress(
                Math.Min(_totalLength, _uploadOffset + _consumed),
                _totalLength));
        }
        return read;
    }
}
