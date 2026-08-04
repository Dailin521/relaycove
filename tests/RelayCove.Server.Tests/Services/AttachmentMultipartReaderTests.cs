using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;

namespace RelayCove.Server.Tests.Services;

public sealed class AttachmentMultipartReaderTests
{
    [Fact]
    public async Task ReadAsync_WhenRequestIsCanceledDuringCopy_DeletesStagingFile()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<AttachmentMultipartReader>();
        var boundary = $"relaycove-{Guid.NewGuid():N}";
        var bytes = CreateRawMultipart(boundary, new byte[256 * 1024]);
        using var cancellation = new CancellationTokenSource();
        await using var requestBody = new CancelingReadStream(
            new MemoryStream(bytes, writable: false),
            cancellation,
            cancelAfterBytes: 32 * 1024);
        var context = new DefaultHttpContext();
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.Body = requestBody;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(context.Request, cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(factory.UploadsPath));
    }

    [Fact]
    public async Task StagedUpload_WhenFinalTargetAlreadyExists_DoesNotOverwriteAndCleansStaging()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var storagePaths = factory.Services.GetRequiredService<AttachmentStoragePaths>();
        await using var staged = storagePaths.CreateStagedUpload(
            "collision.bin",
            "application/octet-stream",
            NullLogger.Instance);
        await File.WriteAllBytesAsync(staged.StagingPath, [1]);
        staged.Complete(1, new string('a', 64));
        await File.WriteAllBytesAsync(staged.FinalPath, [2]);

        Assert.Throws<IOException>(staged.Publish);
        await staged.DisposeAsync();

        Assert.Equal([2], await File.ReadAllBytesAsync(staged.FinalPath));
        Assert.False(File.Exists(staged.StagingPath));
    }

    private static byte[] CreateRawMultipart(string boundary, byte[] content)
    {
        var prefix = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"file\"; filename=\"cancel.bin\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n");
        var suffix = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        return [.. prefix, .. content, .. suffix];
    }

    private sealed class CancelingReadStream(
        Stream inner,
        CancellationTokenSource cancellation,
        long cancelAfterBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            bytesRead += read;
            if (bytesRead >= cancelAfterBytes)
            {
                cancellation.Cancel();
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
