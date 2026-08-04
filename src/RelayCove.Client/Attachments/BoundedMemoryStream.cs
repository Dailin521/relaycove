using System.IO;

namespace RelayCove.Client.Attachments;

/// <summary>
/// A seekable in-memory stream which never grows its retained buffer beyond its declared bound.
/// </summary>
internal sealed class BoundedMemoryStream : Stream
{
    private const int InitialCapacity = 4096;
    private readonly int maximumCapacity;
    private readonly CancellationToken cancellationToken;
    private byte[] buffer = Array.Empty<byte>();
    private int length;
    private int position;
    private bool disposed;

    internal bool LimitExceeded { get; private set; }

    internal bool CancellationObserved { get; private set; }

    public BoundedMemoryStream(long maximumCapacity, CancellationToken cancellationToken = default)
    {
        if (maximumCapacity is < 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapacity));
        }

        this.maximumCapacity = (int)maximumCapacity;
        this.cancellationToken = cancellationToken;
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => !disposed;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return position;
        }
        set
        {
            ThrowIfDisposed();
            if (value is < 0 or > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            position = (int)value;
        }
    }

    public override int Read(byte[] destination, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Read(destination.AsSpan(offset, count));
    }

    public override int Read(Span<byte> destination)
    {
        ThrowIfDisposed();
        var available = Math.Min(destination.Length, length - position);
        if (available <= 0)
        {
            return 0;
        }

        buffer.AsSpan(position, available).CopyTo(destination);
        position += available;
        return available;
    }

    public override int ReadByte()
    {
        ThrowIfDisposed();
        return position >= length ? -1 : buffer[position++];
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        Position = target;
        return position;
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested();
        if (value < 0 || value > maximumCapacity)
        {
            ThrowLimitExceeded();
        }

        var requestedLength = (int)value;
        EnsureCapacity(requestedLength);
        if (requestedLength > length)
        {
            Array.Clear(buffer, length, requestedLength - length);
        }

        length = requestedLength;
        if (position > length)
        {
            position = length;
        }
    }

    public override void Write(byte[] source, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        Write(source.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested();
        if (position > maximumCapacity || source.Length > maximumCapacity - position)
        {
            ThrowLimitExceeded();
        }

        var targetEnd = position + source.Length;
        EnsureCapacity(targetEnd);
        if (position > length)
        {
            Array.Clear(buffer, length, position - length);
        }

        source.CopyTo(buffer.AsSpan(position));
        position = targetEnd;
        if (position > length)
        {
            length = position;
        }
    }

    public override void WriteByte(byte value)
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested();
        if (position >= maximumCapacity)
        {
            ThrowLimitExceeded();
        }

        EnsureCapacity(position + 1);
        if (position > length)
        {
            Array.Clear(buffer, length, position - length);
        }

        buffer[position++] = value;
        if (position > length)
        {
            length = position;
        }
    }

    public override Task WriteAsync(
        byte[] source,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(source, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(source.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    internal byte[] CreateExactSnapshot()
    {
        ThrowIfDisposed();
        ThrowIfCancellationRequested();
        return buffer.AsSpan(0, length).ToArray();
    }

    private void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= buffer.Length)
        {
            return;
        }

        if (requiredCapacity > maximumCapacity)
        {
            ThrowLimitExceeded();
        }

        var doubledCapacity = buffer.Length == 0
            ? InitialCapacity
            : checked(buffer.Length * 2);
        var newCapacity = Math.Min(
            maximumCapacity,
            Math.Max(requiredCapacity, doubledCapacity));
        if (newCapacity < requiredCapacity)
        {
            ThrowLimitExceeded();
        }

        var replacement = new byte[newCapacity];
        buffer.AsSpan(0, length).CopyTo(replacement);
        buffer = replacement;
    }

    private void ThrowLimitExceeded()
    {
        LimitExceeded = true;
        throw new BoundedMemoryStreamLimitExceededException();
    }

    private void ThrowIfCancellationRequested()
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        CancellationObserved = true;
        throw new OperationCanceledException(cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class BoundedMemoryStreamLimitExceededException : IOException
{
    public BoundedMemoryStreamLimitExceededException()
        : base("The bounded memory stream reached its maximum capacity.")
    {
    }
}
