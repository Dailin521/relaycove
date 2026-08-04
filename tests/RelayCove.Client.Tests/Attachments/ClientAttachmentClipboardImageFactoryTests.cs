using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Attachments;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentClipboardImageFactoryTests
{
    private const long TestMaximumSize = 1024 * 1024;

    [Fact]
    public async Task CreateForTestingAsync_WhenBitmapIsNull_ReturnsNoImage()
    {
        var outcome = await ClientAttachmentClipboardImageFactory.CreateForTestingAsync(
            bitmap: null,
            existingSelections: null,
            maximumAttachmentSize: TestMaximumSize);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.NoImage, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenBitmapIsValid_CreatesPngThatCanBeReopenedWithoutChangingBytes()
    {
        var outcome = await CreateOnStaAsync(() => CreateBitmap(3, 2), maximumSize: TestMaximumSize);

        var selection = Assert.IsType<ClientAttachmentDraft>(outcome.Selection);
        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.Success, outcome.Status);
        Assert.True(selection.IsImage);
        Assert.Null(selection.FilePathIdentity);
        Assert.Equal("image/png", selection.Source.ContentType);
        Assert.NotEqual(0, selection.Source.Size);
        Assert.Equal("clipboard-image.png", selection.DisplayName);
        Assert.Equal(selection.Source.Size, selection.RetainedMemoryBytes);

        var first = await ReadAllAsync(selection.Source);
        var second = await ReadAllAsync(selection.Source);

        await using var protectedStream = await selection.Source.OpenReadAsync(CancellationToken.None);
        var memoryStream = Assert.IsType<MemoryStream>(protectedStream);
        Assert.True(memoryStream.CanRead);
        Assert.True(memoryStream.CanSeek);
        Assert.False(memoryStream.CanWrite);
        Assert.Equal(selection.Source.Size, memoryStream.Length);
        Assert.Equal(0, memoryStream.Position);
        Assert.False(memoryStream.TryGetBuffer(out _));

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, first[..8]);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenExistingDraftsHaveRoom_AddsOneWithoutChangingTheirCount()
    {
        var existing = Enumerable.Range(0, 9)
            .Select(index => CreateExistingDraft($"existing-{index}.bin", retainedMemoryBytes: 0))
            .ToArray();

        var outcome = await CreateOnStaAsync(() => CreateBitmap(2, 2), existing, TestMaximumSize);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.Success, outcome.Status);
        Assert.NotNull(outcome.Selection);
        Assert.Equal(10, existing.Append(outcome.Selection!).Count());
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenExistingRetainedMemoryExceedsBound_ReturnsAggregateMemoryTooLarge()
    {
        var existing = new[] { CreateExistingDraft("retained.bin", retainedMemoryBytes: 1025) };

        var outcome = await CreateOnStaAsync(() => CreateBitmap(1, 1), existing, maximumSize: 1024);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenExistingRetainedMemoryEqualsBound_ReturnsAggregateMemoryTooLarge()
    {
        var existing = new[] { CreateExistingDraft("retained.bin", retainedMemoryBytes: 1024) };

        var outcome = await CreateOnStaAsync(() => CreateBitmap(1, 1), existing, maximumSize: 1024);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenPngWouldExceedRemainingDraftMemory_ReturnsAggregateMemoryTooLarge()
    {
        var existing = new[] { CreateExistingDraft("retained.bin", retainedMemoryBytes: 150) };

        var outcome = await CreateOnStaAsync(() => CreateBitmap(1, 1), existing, maximumSize: 200);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenRawBgraPixelsExceedBound_ReturnsRawPixelsTooLarge()
    {
        var outcome = await CreateOnStaAsync(() => CreateBitmap(20, 20), maximumSize: 1024);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.RawPixelsTooLarge, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenPngOutputExceedsBound_ReturnsOutputTooLarge()
    {
        var outcome = await CreateOnStaAsync(() => CreateBitmap(1, 1), maximumSize: 64);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.OutputTooLarge, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenCancellationIsAlreadyRequested_ReturnsCanceled()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var outcome = await CreateOnStaAsync(
            () => CreateBitmap(1, 1),
            maximumSize: TestMaximumSize,
            cancellationToken: cancellationSource.Token);

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.Canceled, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenCanceledAtEncoderBoundary_ReturnsCanceled()
    {
        using var cancellationSource = new CancellationTokenSource();

        var outcome = await RunOnStaAsync(
            () => ClientAttachmentClipboardImageFactory.CreateForTestingAsync(
                CreateBitmap(2, 2),
                existingSelections: null,
                maximumAttachmentSize: TestMaximumSize,
                cancellationSource.Token,
                beforeEncoderSaveForTesting: cancellationSource.Cancel));

        Assert.Equal(ClientAttachmentClipboardImageSelectionStatus.Canceled, outcome.Status);
        Assert.Null(outcome.Selection);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenCalledTwice_CreatesIndependentDraftsWithNonIdentifyingNames()
    {
        var first = await CreateOnStaAsync(() => CreateBitmap(2, 2), maximumSize: TestMaximumSize);
        var second = await CreateOnStaAsync(() => CreateBitmap(2, 2), maximumSize: TestMaximumSize);

        var firstSelection = Assert.IsType<ClientAttachmentDraft>(first.Selection);
        var secondSelection = Assert.IsType<ClientAttachmentDraft>(second.Selection);
        Assert.NotEqual(firstSelection.DraftId, secondSelection.DraftId);
        Assert.Equal("clipboard-image.png", firstSelection.DisplayName);
        Assert.Equal(firstSelection.DisplayName, secondSelection.DisplayName);
    }

    [Fact]
    public async Task MaterializeForTesting_WhenSourceChangesAfterSnapshot_PreservesOriginalPixels()
    {
        var result = await RunOnStaAsync(
            () =>
            {
                var source = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, palette: null);
                var originalPixels = new byte[] { 1, 2, 3, 255 };
                source.WritePixels(new System.Windows.Int32Rect(0, 0, 1, 1), originalPixels, 4, 0);

                var snapshot = Assert.IsType<CachedBitmap>(
                    ClientAttachmentClipboardImageFactory.MaterializeForTesting(
                        source,
                        maximumRawBgra32Bytes: 4));
                source.WritePixels(
                    new System.Windows.Int32Rect(0, 0, 1, 1),
                    new byte[] { 9, 8, 7, 255 },
                    4,
                    0);

                var snapshotPixels = new byte[4];
                snapshot.CopyPixels(snapshotPixels, 4, 0);
                return Task.FromResult((snapshotPixels, snapshot.IsFrozen));
            });

        Assert.Equal(new byte[] { 1, 2, 3, 255 }, result.snapshotPixels);
        Assert.True(result.IsFrozen);
    }

    [Fact]
    public void CreateAsync_WhenUsingProductionPolicy_UsesDefaultServerAndAbsoluteRawBudgets()
    {
        Assert.Equal(25L * 1024 * 1024, ClientAttachmentClipboardImageFactory.MaximumRetainedPngBytes);
        Assert.Equal(100L * 1024 * 1024, ClientAttachmentClipboardImageFactory.MaximumRawBgra32Bytes);
    }

    [Fact]
    public async Task CreateForTestingAsync_WhenSelectionIsRenderedAsText_RedactsAttachmentDetails()
    {
        var outcome = await CreateOnStaAsync(() => CreateBitmap(3, 2), maximumSize: TestMaximumSize);
        var selection = Assert.IsType<ClientAttachmentDraft>(outcome.Selection);

        var outcomeText = outcome.ToString();
        var selectionText = selection.ToString();

        Assert.DoesNotContain(selection.DisplayName, outcomeText, StringComparison.Ordinal);
        Assert.DoesNotContain(selection.DisplayName, selectionText, StringComparison.Ordinal);
        Assert.DoesNotContain(selection.Source.Size.ToString(), selectionText, StringComparison.Ordinal);
        Assert.DoesNotContain(selection.DraftId.ToString(), selectionText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcomeText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", selectionText, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedMemoryStream_WhenCanceledBeforeWrite_ThrowsCancellationWithoutGrowing()
    {
        using var cancellationSource = new CancellationTokenSource();
        using var stream = new BoundedMemoryStream(128, cancellationSource.Token);
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => stream.WriteByte(1));
        Assert.Equal(0, stream.Length);
        Assert.True(stream.CancellationObserved);
    }

    [Fact]
    public void BoundedMemoryStream_WhenExactLimitIsWritten_SnapshotsExactBytesThenRejectsGrowth()
    {
        using var stream = new BoundedMemoryStream(4);

        stream.Write(new byte[] { 1, 2, 3, 4 });
        var snapshot = stream.CreateExactSnapshot();
        var exception = Assert.Throws<BoundedMemoryStreamLimitExceededException>(
            () => stream.WriteByte(5));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, snapshot);
        Assert.NotNull(exception);
        Assert.True(stream.LimitExceeded);
    }

    private static async Task<ClientAttachmentClipboardImageSelectionOutcome> CreateOnStaAsync(
        Func<BitmapSource> bitmapFactory,
        IReadOnlyList<ClientAttachmentDraft>? existingSelections = null,
        long maximumSize = TestMaximumSize,
        CancellationToken cancellationToken = default) =>
        await RunOnStaAsync(
            () => ClientAttachmentClipboardImageFactory.CreateForTestingAsync(
                bitmapFactory(),
                existingSelections,
                maximumSize,
                cancellationToken));

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index % byte.MaxValue);
            pixels[index + 1] = (byte)((index + 1) % byte.MaxValue);
            pixels[index + 2] = (byte)((index + 2) % byte.MaxValue);
            pixels[index + 3] = byte.MaxValue;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static ClientAttachmentDraft CreateExistingDraft(string fileName, long retainedMemoryBytes)
    {
        var source = new ClientAttachmentUploadSource(
            fileName,
            "application/octet-stream",
            size: 1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)));
        return new ClientAttachmentDraft(
            Guid.NewGuid(),
            source,
            fileName,
            "1 B",
            isImage: false,
            filePathIdentity: null,
            retainedMemoryBytes: retainedMemoryBytes);
    }

    private static async Task<byte[]> ReadAllAsync(ClientAttachmentUploadSource source)
    {
        await using var stream = await source.OpenReadAsync(CancellationToken.None);
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static Task<T> RunOnStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.TrySetResult(action().GetAwaiter().GetResult());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
