using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentImageDecoder
{
    public static async Task<ClientAttachmentImageDecodeResult> DecodeAsync(
        Stream stream,
        ClientAttachmentImageRendition rendition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanRead || !stream.CanSeek || stream.Position != 0)
        {
            return ClientAttachmentImageDecodeResult.Failure(
                ClientAttachmentImageDecodeStatus.InvalidInput);
        }

        ClientAttachmentImageSignature signature;
        try
        {
            signature = ReadSignature(stream);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return ClientAttachmentImageDecodeResult.Failure(
                ClientAttachmentImageDecodeStatus.DecodeFailed);
        }

        if (signature == ClientAttachmentImageSignature.Unknown)
        {
            return ClientAttachmentImageDecodeResult.Failure(
                ClientAttachmentImageDecodeStatus.UnsupportedFormat);
        }

        if (stream.Length > ClientAttachmentImageDecodePolicy.MaximumInputBytes)
        {
            return ClientAttachmentImageDecodeResult.Failure(
                ClientAttachmentImageDecodeStatus.SourceTooLarge);
        }

        try
        {
            await using var protectedStream = new NonDisposingReadStream(stream);
            using var randomAccessStream = protectedStream.AsRandomAccessStream();
            var decoder = await WinBitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (decoder.DecoderInformation.CodecId != GetExpectedCodecId(signature))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.UnsupportedCodec);
            }

            if (!ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(
                    decoder.PixelWidth,
                    decoder.PixelHeight))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.SourceTooLarge);
            }

            var sourcePixels = checked((long)decoder.PixelWidth * decoder.PixelHeight);
            if (signature == ClientAttachmentImageSignature.Png &&
                !ClientAttachmentImageDecodePolicy.IsPngCompressionWithinBudget(
                    sourcePixels,
                    stream.Length))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.SourceTooLarge);
            }

            var frame = await decoder.GetFrameAsync(0).AsTask(cancellationToken).ConfigureAwait(false);
            if (!ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(
                    frame.PixelWidth,
                    frame.PixelHeight))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.SourceTooLarge);
            }

            var targetSize = ClientAttachmentImageDecodePolicy.GetTargetSize(
                frame.PixelWidth,
                frame.PixelHeight,
                rendition);
            if (!ClientAttachmentImageDecodePolicy.IsOutputWithinBudget(targetSize))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.OutputTooLarge);
            }

            var transform = new BitmapTransform
            {
                ScaledWidth = checked((uint)targetSize.PixelWidth),
                ScaledHeight = checked((uint)targetSize.PixelHeight),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var softwareBitmap = await frame.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var actualSize = new ClientAttachmentImageSafeSize(
                softwareBitmap.PixelWidth,
                softwareBitmap.PixelHeight);
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied ||
                !ClientAttachmentImageDecodePolicy.IsOutputWithinBudget(actualSize) ||
                !IsExpectedOutputSize(actualSize, targetSize) ||
                actualSize.PixelWidth > GetMaximumEdge(rendition) ||
                actualSize.PixelHeight > GetMaximumEdge(rendition))
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.OutputTooLarge);
            }

            var byteCount = checked(actualSize.PixelCount * 4);
            var buffer = new Windows.Storage.Streams.Buffer(checked((uint)byteCount));
            softwareBitmap.CopyToBuffer(buffer);
            if (buffer.Capacity != byteCount || buffer.Length != byteCount)
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.DecodeFailed);
            }

            var pixels = buffer.ToArray();
            if (pixels.LongLength != byteCount)
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.DecodeFailed);
            }

            var image = BitmapSource.Create(
                actualSize.PixelWidth,
                actualSize.PixelHeight,
                96,
                96,
                PixelFormats.Pbgra32,
                palette: null,
                pixels,
                checked(actualSize.PixelWidth * 4));
            if (!image.CanFreeze)
            {
                return ClientAttachmentImageDecodeResult.Failure(
                    ClientAttachmentImageDecodeStatus.DecodeFailed);
            }

            image.Freeze();
            return ClientAttachmentImageDecodeResult.Success(
                image,
                wasDownsampled: actualSize != new ClientAttachmentImageSafeSize(
                    checked((int)frame.OrientedPixelWidth),
                    checked((int)frame.OrientedPixelHeight)),
                actualSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return ClientAttachmentImageDecodeResult.Failure(
                ClientAttachmentImageDecodeStatus.DecodeFailed);
        }
        finally
        {
            try
            {
                stream.Position = 0;
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                // The caller owns this stream. A failed best-effort rewind must not leak or close it.
            }
        }
    }

    private static ClientAttachmentImageSignature ReadSignature(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        var length = 0;
        while (length < header.Length)
        {
            var read = stream.Read(header[length..]);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        stream.Position = 0;
        return header[..length] switch
        {
            [137, 80, 78, 71, 13, 10, 26, 10] => ClientAttachmentImageSignature.Png,
            [255, 216, 255, ..] => ClientAttachmentImageSignature.Jpeg,
            _ => ClientAttachmentImageSignature.Unknown,
        };
    }

    private static Guid GetExpectedCodecId(ClientAttachmentImageSignature signature) =>
        signature switch
        {
            ClientAttachmentImageSignature.Png => WinBitmapDecoder.PngDecoderId,
            ClientAttachmentImageSignature.Jpeg => WinBitmapDecoder.JpegDecoderId,
            _ => throw new ArgumentOutOfRangeException(nameof(signature)),
        };

    private static int GetMaximumEdge(ClientAttachmentImageRendition rendition) =>
        rendition switch
        {
            ClientAttachmentImageRendition.Thumbnail =>
                ClientAttachmentImageDecodePolicy.ThumbnailMaximumEdge,
            ClientAttachmentImageRendition.Viewer => ClientAttachmentImageDecodePolicy.ViewerMaximumEdge,
            _ => throw new ArgumentOutOfRangeException(nameof(rendition)),
        };

    private static bool IsExpectedOutputSize(
        ClientAttachmentImageSafeSize actualSize,
        ClientAttachmentImageSafeSize targetSize) =>
        actualSize == targetSize ||
        (actualSize.PixelWidth == targetSize.PixelHeight &&
         actualSize.PixelHeight == targetSize.PixelWidth);

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private enum ClientAttachmentImageSignature
    {
        Unknown = 0,
        Png = 1,
        Jpeg = 2,
    }

    private sealed class NonDisposingReadStream : Stream
    {
        private readonly Stream inner;

        public NonDisposingReadStream(Stream inner)
        {
            this.inner = inner;
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());

        protected override void Dispose(bool disposing)
        {
        }
    }
}
