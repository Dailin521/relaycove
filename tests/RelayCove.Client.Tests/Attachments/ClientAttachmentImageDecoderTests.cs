using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Attachments;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentImageDecoderTests
{
    [Fact]
    public async Task DecodeAsync_WhenPngIsValid_CreatesFrozenThumbnailAndLeavesStreamOpen()
    {
        var png = await RunOnStaAsync(() => Task.FromResult(CreatePng(640, 320)));
        using var stream = new MemoryStream(png, writable: false);

        var result = await RunOnStaAsync(
            () => ClientAttachmentImageDecoder.DecodeAsync(
                stream,
                ClientAttachmentImageRendition.Thumbnail));

        var image = Assert.IsAssignableFrom<BitmapSource>(result.Image);
        Assert.Equal(ClientAttachmentImageDecodeStatus.Success, result.Status);
        Assert.True(result.WasDownsampled);
        Assert.Equal(new ClientAttachmentImageSafeSize(320, 160), result.SafeSize);
        Assert.Equal(320, image.PixelWidth);
        Assert.Equal(160, image.PixelHeight);
        Assert.True(image.IsFrozen);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenJpegIsValid_CreatesFrozenBoundedViewerImage()
    {
        var jpeg = await RunOnStaAsync(() => Task.FromResult(CreateJpeg(800, 400)));
        using var stream = new MemoryStream(jpeg, writable: false);

        var result = await RunOnStaAsync(
            () => ClientAttachmentImageDecoder.DecodeAsync(
                stream,
                ClientAttachmentImageRendition.Viewer));

        var image = Assert.IsAssignableFrom<BitmapSource>(result.Image);
        Assert.Equal(ClientAttachmentImageDecodeStatus.Success, result.Status);
        Assert.False(result.WasDownsampled);
        Assert.Equal(new ClientAttachmentImageSafeSize(800, 400), result.SafeSize);
        Assert.Equal(800, image.PixelWidth);
        Assert.Equal(400, image.PixelHeight);
        Assert.True(image.IsFrozen);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenFormatSignatureIsUnknown_ReturnsUnsupportedFormatWithoutClosingStream()
    {
        using var stream = new MemoryStream([1, 2, 3, 4], writable: false);

        var result = await ClientAttachmentImageDecoder.DecodeAsync(
            stream,
            ClientAttachmentImageRendition.Thumbnail);

        Assert.Equal(ClientAttachmentImageDecodeStatus.UnsupportedFormat, result.Status);
        Assert.Null(result.Image);
        Assert.False(result.WasDownsampled);
        Assert.Null(result.SafeSize);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenPngPayloadIsCorrupt_ReturnsDecodeFailed()
    {
        using var stream = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10], writable: false);

        var result = await ClientAttachmentImageDecoder.DecodeAsync(
            stream,
            ClientAttachmentImageRendition.Viewer);

        Assert.Equal(ClientAttachmentImageDecodeStatus.DecodeFailed, result.Status);
        Assert.Null(result.Image);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenValidPngIsTooCompressedForItsSourcePixels_ReturnsSourceTooLarge()
    {
        const uint width = 4_096;
        const uint height = 4_096;
        var png = CreateGrayscalePng(width, height);
        using var stream = new MemoryStream(png, writable: false);

        Assert.True(ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(width, height));
        Assert.False(ClientAttachmentImageDecodePolicy.IsPngCompressionWithinBudget(
            checked((long)width * height),
            png.Length));

        var result = await RunOnStaAsync(
            () => ClientAttachmentImageDecoder.DecodeAsync(
                stream,
                ClientAttachmentImageRendition.Thumbnail));

        Assert.Equal(ClientAttachmentImageDecodeStatus.SourceTooLarge, result.Status);
        Assert.Null(result.Image);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenValidPngDimensionsExceedBudgetDespiteAcceptableCompression_ReturnsSourceTooLarge()
    {
        const uint width = 4_097;
        const uint height = 4_096;
        var sourcePixels = checked((long)width * height);
        var png = CreateGrayscalePng(
            width,
            height,
            minimumInputBytes: checked((sourcePixels +
                ClientAttachmentImageDecodePolicy.MaximumPngPixelsPerInputByte - 1) /
                ClientAttachmentImageDecodePolicy.MaximumPngPixelsPerInputByte));
        using var stream = new MemoryStream(png, writable: false);

        Assert.False(ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(width, height));
        Assert.True(ClientAttachmentImageDecodePolicy.IsPngCompressionWithinBudget(sourcePixels, png.Length));

        var result = await RunOnStaAsync(
            () => ClientAttachmentImageDecoder.DecodeAsync(
                stream,
                ClientAttachmentImageRendition.Viewer));

        Assert.Equal(ClientAttachmentImageDecodeStatus.SourceTooLarge, result.Status);
        Assert.Null(result.Image);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DecodeAsync_WhenCanceled_PropagatesCancellationWithoutClosingStream()
    {
        using var cancellationSource = new CancellationTokenSource();
        var png = await RunOnStaAsync(() => Task.FromResult(CreatePng(1, 1)));
        using var stream = new MemoryStream(png, writable: false);
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ClientAttachmentImageDecoder.DecodeAsync(
                stream,
                ClientAttachmentImageRendition.Thumbnail,
                cancellationSource.Token));

        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void GetTargetSize_WhenRenditionHasAnEdgeLimit_PreservesAspectRatioWithinBudget()
    {
        var thumbnail = ClientAttachmentImageDecodePolicy.GetTargetSize(
            32_768,
            16_384,
            ClientAttachmentImageRendition.Thumbnail);
        var viewer = ClientAttachmentImageDecodePolicy.GetTargetSize(
            32_768,
            1,
            ClientAttachmentImageRendition.Viewer);

        Assert.Equal(new ClientAttachmentImageSafeSize(320, 160), thumbnail);
        Assert.Equal(new ClientAttachmentImageSafeSize(2_560, 1), viewer);
        Assert.True(ClientAttachmentImageDecodePolicy.IsOutputWithinBudget(thumbnail));
        Assert.True(ClientAttachmentImageDecodePolicy.IsOutputWithinBudget(viewer));
    }

    [Fact]
    public void IsSourceWithinBudget_WhenEdgeOrPixelsExceedBound_ReturnsFalse()
    {
        Assert.True(ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(4_096, 4_096));
        Assert.False(ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(16_385, 1));
        Assert.False(ClientAttachmentImageDecodePolicy.IsSourceWithinBudget(4_097, 4_096));
    }

    [Fact]
    public async Task DecodeAsync_WhenGifOrBmpSignatureIsUsed_ReturnsUnsupportedFormat()
    {
        using var gif = new MemoryStream("GIF89a"u8.ToArray(), writable: false);
        using var bmp = new MemoryStream(new byte[] { (byte)'B', (byte)'M', 0, 0 }, writable: false);

        var gifResult = await ClientAttachmentImageDecoder.DecodeAsync(
            gif,
            ClientAttachmentImageRendition.Thumbnail);
        var bmpResult = await ClientAttachmentImageDecoder.DecodeAsync(
            bmp,
            ClientAttachmentImageRendition.Thumbnail);

        Assert.Equal(ClientAttachmentImageDecodeStatus.UnsupportedFormat, gifResult.Status);
        Assert.Equal(ClientAttachmentImageDecodeStatus.UnsupportedFormat, bmpResult.Status);
    }

    [Theory]
    [InlineData(65_536, 256, true)]
    [InlineData(65_537, 256, false)]
    public void IsPngCompressionWithinBudget_WhenRatioCrossesLimit_ReturnsExpected(
        long sourcePixels,
        long inputBytes,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClientAttachmentImageDecodePolicy.IsPngCompressionWithinBudget(
                sourcePixels,
                inputBytes));
    }

    [Fact]
    public void DecodeResult_WhenRenderedAsText_RedactsImageAndDimensions()
    {
        var image = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Pbgra32,
            palette: null,
            new byte[] { 1, 2, 3, 255 },
            stride: 4);
        image.Freeze();
        var result = ClientAttachmentImageDecodeResult.Success(
            image,
            wasDownsampled: true,
            new ClientAttachmentImageSafeSize(1, 1));

        var rendered = result.ToString();

        Assert.Contains("Status = Success", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelWidth", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelHeight", rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
    }

    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 1;
            pixels[index + 1] = 2;
            pixels[index + 2] = 3;
            pixels[index + 3] = byte.MaxValue;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: checked(width * 4));
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 16;
            pixels[index + 1] = 64;
            pixels[index + 2] = 128;
            pixels[index + 3] = byte.MaxValue;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: checked(width * 4));
        source.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] CreateGrayscalePng(uint width, uint height, long minimumInputBytes = 0)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        WritePngChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[checked((int)width + 1)];
            for (var rowIndex = 0U; rowIndex < height; rowIndex++)
            {
                zlib.Write(row);
            }
        }

        WritePngChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));

        const int chunkOverhead = 12;
        if (output.Length + chunkOverhead < minimumInputBytes)
        {
            var paddingLength = checked((int)(minimumInputBytes - output.Length - chunkOverhead));
            WritePngChunk(output, "raNd"u8, new byte[paddingLength]);
        }

        WritePngChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        output.Write(type);
        output.Write(data);

        Span<byte> crc = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(crc, CalculatePngCrc(type, data));
        output.Write(crc);
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        crc = UpdatePngCrc(crc, type);
        crc = UpdatePngCrc(crc, data);
        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return crc;
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
