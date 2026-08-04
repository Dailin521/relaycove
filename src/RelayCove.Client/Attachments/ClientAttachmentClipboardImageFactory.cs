using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentClipboardImageFactory
{
    internal const long MaximumRetainedPngBytes = 25L * 1024 * 1024;
    internal const long MaximumRawBgra32Bytes =
        ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize;
    private const string ClipboardImageFileName = "clipboard-image.png";

    public static Task<ClientAttachmentClipboardImageSelectionOutcome> CreateAsync(
        BitmapSource? bitmap,
        IReadOnlyList<ClientAttachmentDraft>? existingSelections = null,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(
            bitmap,
            existingSelections,
            MaximumRetainedPngBytes,
            MaximumRawBgra32Bytes,
            cancellationToken,
            beforeEncoderSaveForTesting: null);

    internal static Task<ClientAttachmentClipboardImageSelectionOutcome> CreateForTestingAsync(
        BitmapSource? bitmap,
        IReadOnlyList<ClientAttachmentDraft>? existingSelections,
        long maximumAttachmentSize,
        CancellationToken cancellationToken = default,
        Action? beforeEncoderSaveForTesting = null) =>
        CreateCoreAsync(
            bitmap,
            existingSelections,
            maximumAttachmentSize,
            maximumAttachmentSize,
            cancellationToken,
            beforeEncoderSaveForTesting);

    internal static BitmapSource? MaterializeForTesting(
        BitmapSource bitmap,
        long maximumRawBgra32Bytes) =>
        MaterializeOnCallingThread(bitmap, maximumRawBgra32Bytes);

    private static async Task<ClientAttachmentClipboardImageSelectionOutcome> CreateCoreAsync(
        BitmapSource? bitmap,
        IReadOnlyList<ClientAttachmentDraft>? existingSelections,
        long maximumRetainedPngBytes,
        long maximumRawBgra32Bytes,
        CancellationToken cancellationToken,
        Action? beforeEncoderSaveForTesting)
    {
        if (maximumRetainedPngBytes is < 1 or
            > ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedPngBytes));
        }

        if (maximumRawBgra32Bytes is < 1 or
            > ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRawBgra32Bytes));
        }

        if (bitmap is null)
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.NoImage);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingSnapshot = existingSelections?.ToArray() ?? [];
            if (existingSnapshot.Any(static selection => selection is null) ||
                existingSnapshot.Length >= ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage)
            {
                return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                    ClientAttachmentClipboardImageSelectionStatus.TooManyFiles);
            }

            var existingRetainedMemory = SumRetainedMemory(
                existingSnapshot,
                maximumRetainedPngBytes);
            if (existingRetainedMemory >= maximumRetainedPngBytes)
            {
                return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                    ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge);
            }

            var materializedBitmap = MaterializeOnCallingThread(
                bitmap,
                maximumRawBgra32Bytes);
            if (materializedBitmap is null)
            {
                return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                    ClientAttachmentClipboardImageSelectionStatus.RawPixelsTooLarge);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(
                    () => Encode(
                        materializedBitmap,
                        maximumRetainedPngBytes,
                        maximumRetainedPngBytes - existingRetainedMemory,
                        cancellationToken,
                        beforeEncoderSaveForTesting),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.Canceled);
        }
        catch (BoundedMemoryStreamLimitExceededException)
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.OutputTooLarge);
        }
        catch (ClipboardImageAggregateLimitExceededException)
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge);
        }
        catch (ClipboardImageInvalidException)
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.InvalidImage);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return ClientAttachmentClipboardImageSelectionOutcome.Failure(
                ClientAttachmentClipboardImageSelectionStatus.EncodingFailed);
        }
    }

    private static BitmapSource? MaterializeOnCallingThread(
        BitmapSource bitmap,
        long maximumRawBgra32Bytes)
    {
        try
        {
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return null;
            }

            long rawBgra32Bytes;
            try
            {
                rawBgra32Bytes = checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
            }
            catch (OverflowException)
            {
                return null;
            }

            if (rawBgra32Bytes > maximumRawBgra32Bytes)
            {
                return null;
            }

            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = bitmap;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            var materialized = new CachedBitmap(
                converted,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            if (!materialized.CanFreeze)
            {
                throw new InvalidOperationException("The clipboard image cannot be frozen.");
            }

            materialized.Freeze();
            return materialized;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            throw new ClipboardImageInvalidException(exception);
        }
    }

    private static ClientAttachmentClipboardImageSelectionOutcome Encode(
        BitmapSource materializedBitmap,
        long maximumRetainedPngBytes,
        long availableRetainedMemory,
        CancellationToken cancellationToken,
        Action? beforeEncoderSaveForTesting)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new BoundedMemoryStream(availableRetainedMemory, cancellationToken);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(
            BitmapFrame.Create(
                materializedBitmap,
                thumbnail: null,
                metadata: null,
                colorContexts: null));
        beforeEncoderSaveForTesting?.Invoke();
        try
        {
            encoder.Save(output);
        }
        catch (Exception exception) when (
            output.CancellationObserved &&
            cancellationToken.IsCancellationRequested &&
            !IsCriticalException(exception))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (
            output.LimitExceeded && !IsCriticalException(exception))
        {
            if (availableRetainedMemory < maximumRetainedPngBytes)
            {
                throw new ClipboardImageAggregateLimitExceededException();
            }

            throw new BoundedMemoryStreamLimitExceededException();
        }
        cancellationToken.ThrowIfCancellationRequested();

        var size = output.Length;
        if (size is < 1 || size > maximumRetainedPngBytes)
        {
            throw new BoundedMemoryStreamLimitExceededException();
        }

        var retainedBuffer = output.CreateExactSnapshot();
        var source = new ClientAttachmentUploadSource(
            ClipboardImageFileName,
            "image/png",
            size,
            token => OpenReadAsync(retainedBuffer, size, token));
        var selection = new ClientAttachmentDraft(
            Guid.NewGuid(),
            source,
            source.OriginalFileName,
            FormatDisplaySize(source.Size),
            isImage: true,
            filePathIdentity: null,
            retainedMemoryBytes: size);
        return ClientAttachmentClipboardImageSelectionOutcome.Success(selection);
    }

    private static ValueTask<Stream> OpenReadAsync(
        byte[] retainedBuffer,
        long size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(
            new MemoryStream(
                retainedBuffer,
                0,
                checked((int)size),
                writable: false,
                publiclyVisible: false));
    }

    private static long SumRetainedMemory(
        IReadOnlyList<ClientAttachmentDraft> selections,
        long maximumAttachmentSize)
    {
        long total = 0;
        foreach (var selection in selections)
        {
            total = checked(total + selection.RetainedMemoryBytes);
            if (total > maximumAttachmentSize)
            {
                return total;
            }
        }

        return total;
    }

    private static string FormatDisplaySize(long size) =>
        size switch
        {
            < 1024 => $"{size.ToString(CultureInfo.InvariantCulture)} B",
            < 1024 * 1024 =>
                $"{(size / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KiB",
            _ => $"{(size / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MiB",
        };

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class ClipboardImageInvalidException : Exception
    {
        public ClipboardImageInvalidException(Exception innerException)
            : base("The clipboard image is invalid.", innerException)
        {
        }
    }

    private sealed class ClipboardImageAggregateLimitExceededException : Exception
    {
    }
}
