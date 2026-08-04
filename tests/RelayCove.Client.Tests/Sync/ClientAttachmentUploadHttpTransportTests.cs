using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

public sealed class ClientAttachmentUploadHttpTransportTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");

    [Fact]
    public async Task UploadAsync_WhenCreated_SendsOneStreamingFilePartAndValidatesUnicodeMetadata()
    {
        var sourceBytes = "payload"u8.ToArray();
        var source = CreateSource("照片 名称.png", "image/png", sourceBytes);
        var attachmentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri(ServerBaseUri, "api/attachments"), request.RequestUri);
            Assert.Equal("access-token", request.Headers.Authorization!.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var part = Assert.Single(multipart);
            Assert.Equal("file", part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.True(
                string.Equals(
                    "照片 名称.png",
                    part.Headers.ContentDisposition.FileName?.Trim('"'),
                    StringComparison.Ordinal) ||
                string.Equals(
                    "照片 名称.png",
                    part.Headers.ContentDisposition.FileNameStar,
                    StringComparison.Ordinal));
            Assert.Equal("image/png", part.Headers.ContentType!.MediaType);
            Assert.Equal(sourceBytes, await part.ReadAsByteArrayAsync(cancellationToken));
            return Created(attachmentId, source);
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.Success, result.Status);
        Assert.Equal(attachmentId, result.Attachment!.Id);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(100L * 1024 * 1024)]
    public void Constructor_WhenSizeIsAtClientBoundary_AcceptsSource(long size)
    {
        var source = new ClientAttachmentUploadSource(
            "boundary.bin",
            "application/octet-stream",
            size,
            _ => ValueTask.FromResult<Stream>(new LengthOnlyStream(size)));

        Assert.Equal(size, source.Size);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(100L * 1024 * 1024 + 1)]
    public void Constructor_WhenSizeIsOutsideClientBoundary_RejectsSource(long size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientAttachmentUploadSource(
            "boundary.bin",
            "application/octet-stream",
            size,
            _ => ValueTask.FromResult<Stream>(new LengthOnlyStream(1))));

    [Theory]
    [InlineData(" name.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("name\u0001.txt")]
    public void Constructor_WhenFileNameIsUnsafe_RejectsSource(string fileName) =>
        Assert.Throws<ArgumentException>(() => CreateSource(fileName, "text/plain", [1]));

    [Theory]
    [InlineData("Image/PNG")]
    [InlineData("image/png; charset=utf-8")]
    [InlineData("image/*")]
    public void Constructor_WhenContentTypeIsNotCanonical_RejectsSource(string contentType) =>
        Assert.Throws<ArgumentException>(() => CreateSource("photo.png", contentType, [1]));

    [Fact]
    public async Task UploadAsync_WhenStableAuthenticationRequired_ReopensOnceAfterRefresh()
    {
        var source = CreateTrackingSource("retry.png", "image/png", [1, 2, 3]);
        var attachmentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var authentication = new FakeAuthenticationSession("old-token", "new-token");
        var authorizationTokens = new List<string?>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            authorizationTokens.Add(request.Headers.Authorization!.Parameter);
            return Task.FromResult(authorizationTokens.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = JsonContent.Create(new ApiErrorResponse(
                        ApiErrorCodes.AuthenticationRequired,
                        "Authentication is required.")),
                }
                : Created(attachmentId, source.Source));
        }));

        var result = await CreateTransport(httpClient, authentication)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.Success, result.Status);
        Assert.Equal(["old-token", "new-token"], authorizationTokens);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, source.OpenCount);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenContentIsCopied_ReportsAttemptBytesAndIsolatesCallbackFailure()
    {
        var source = CreateTrackingSource("progress.bin", "application/octet-stream", [1, 2, 3, 4]);
        var copied = new List<long>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal([1, 2, 3, 4], await Assert.Single(multipart).ReadAsByteArrayAsync(token));
            return Created(Guid.NewGuid(), source.Source);
        }));

        var result = await CreateTransport(httpClient).UploadAsync(
            source.Source,
            CancellationToken.None,
            bytes =>
            {
                copied.Add(bytes);
                throw new InvalidOperationException("receiver failure");
            });

        Assert.Equal(ClientAttachmentUploadHttpStatus.Success, result.Status);
        Assert.Equal(source.Source.Size, Assert.Single(copied));
        Assert.True(Assert.Single(source.OpenedStreams).Disposed);
    }

    [Fact]
    public async Task UploadAsync_WhenStable401AfterContentCopy_ReportsEachReopenedAttempt()
    {
        var source = CreateTrackingSource("retry-progress.bin", "application/octet-stream", [1, 2, 3]);
        var authentication = new FakeAuthenticationSession("old-token", "new-token");
        var copied = new List<long>();
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            _ = await Assert.Single(multipart).ReadAsByteArrayAsync(token);
            if (Interlocked.Increment(ref requests) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = JsonContent.Create(new ApiErrorResponse(
                        ApiErrorCodes.AuthenticationRequired,
                        "Authentication is required.")),
                };
            }

            return Created(Guid.NewGuid(), source.Source);
        }));

        var result = await CreateTransport(httpClient, authentication).UploadAsync(
            source.Source,
            CancellationToken.None,
            copied.Add);

        Assert.Equal(ClientAttachmentUploadHttpStatus.Success, result.Status);
        Assert.Equal([3, 3], copied);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenUnauthorizedEnvelopeIsMalformed_DoesNotRefreshOrReplay()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var authentication = new FakeAuthenticationSession("access-token", "refreshed-token");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("<html>gateway</html>", Encoding.UTF8, "text/html"),
            });
        }));

        var result = await CreateTransport(httpClient, authentication)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.Equal(0, authentication.RefreshCount);
        Assert.Equal(1, source.OpenCount);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenUnauthorizedBodyIsEmpty_DoesNotRefreshOrReplay()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var authentication = new FakeAuthenticationSession("access-token", "refreshed-token");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));

        var result = await CreateTransport(httpClient, authentication)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.Equal(0, authentication.RefreshCount);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenUnauthorizedEnvelopeHasDifferentCode_DoesNotRefreshOrReplay()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var authentication = new FakeAuthenticationSession("access-token", "refreshed-token");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    ApiErrorCodes.AuthenticationFailed,
                    "Authentication failed.")),
            });
        }));

        var result = await CreateTransport(httpClient, authentication)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.Equal(0, authentication.RefreshCount);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenUnauthorizedJsonEnvelopeExceedsBound_DoesNotRefreshOrReplay()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var authentication = new FakeAuthenticationSession("access-token", "refreshed-token");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    new string('x', 16 * 1024 + 1),
                    Encoding.UTF8,
                    "application/json"),
            });
        }));

        var result = await CreateTransport(httpClient, authentication)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.Equal(0, authentication.RefreshCount);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, (int)ClientAttachmentUploadHttpStatus.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, (int)ClientAttachmentUploadHttpStatus.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, (int)ClientAttachmentUploadHttpStatus.ValidationFailed)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, (int)ClientAttachmentUploadHttpStatus.AttachmentTooLarge)]
    [InlineData(HttpStatusCode.TemporaryRedirect, (int)ClientAttachmentUploadHttpStatus.RemoteFailure)]
    [InlineData(HttpStatusCode.PermanentRedirect, (int)ClientAttachmentUploadHttpStatus.RemoteFailure)]
    public async Task UploadAsync_WhenNonIdempotentHttpFailure_DoesNotReplay(
        HttpStatusCode statusCode,
        int expectedStatusValue)
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal((ClientAttachmentUploadHttpStatus)expectedStatusValue, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenRequestFailsTransiently_DoesNotReplayAndDisposesStream()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new HttpRequestException("transport unavailable");
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.TransientFailure, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenHttpTimeoutOccurs_DoesNotReplayAndDisposesStream()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new OperationCanceledException("Simulated HttpClient timeout.");
        }));

        var result = await CreateTransport(httpClient)
            .UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.TransientFailure, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenCallerCancelsInFlight_DoesNotReplayAndDisposesStream()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var requestCount = 0;
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, token) =>
        {
            Interlocked.Increment(ref requestCount);
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        }));

        var result = await CreateTransport(httpClient)
            .UploadAsync(source.Source, cancellation.Token);

        Assert.Equal(ClientAttachmentUploadHttpStatus.Canceled, result.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UploadAsync_WhenSourceRemainingLengthDoesNotMatch_DoesNotPostAndDisposesStream()
    {
        var opened = new TrackingMemoryStream([1, 2]);
        var source = new ClientAttachmentUploadSource(
            "one.bin",
            "application/octet-stream",
            1,
            _ => ValueTask.FromResult<Stream>(opened));
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("An invalid source must not post.");
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.SourceUnavailable, result.Status);
        Assert.Equal(0, Volatile.Read(ref requestCount));
        Assert.True(opened.Disposed);
    }

    [Fact]
    public async Task UploadAsync_WhenSourceLengthGetterThrows_DisposesStreamAndDoesNotPost()
    {
        var opened = new ThrowingLengthStream();
        var source = new ClientAttachmentUploadSource(
            "one.bin",
            "application/octet-stream",
            1,
            _ => ValueTask.FromResult<Stream>(opened));
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("An invalid source must not post.");
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.SourceUnavailable, result.Status);
        Assert.Equal(0, Volatile.Read(ref requestCount));
        Assert.True(opened.Disposed);
    }

    [Fact]
    public async Task UploadAsync_WhenCreatedPayloadOrLocationIsInvalid_RejectsItAndDisposesStream()
    {
        var source = CreateTrackingSource("one.bin", "application/octet-stream", [1]);
        var attachmentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Created(attachmentId, source.Source);
            response.Headers.Location = new Uri("/api/attachments/not-the-id", UriKind.Relative);
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).UploadAsync(source.Source, CancellationToken.None);

        Assert.Equal(ClientAttachmentUploadHttpStatus.ProtocolError, result.Status);
        Assert.All(source.OpenedStreams, stream => Assert.True(stream.Disposed));
    }

    private static ClientAttachmentUploadHttpTransport CreateTransport(
        HttpClient httpClient,
        IClientAuthenticationSession? authenticationSession = null) =>
        new(
            AccountScopeIdentity.Create(ServerBaseUri, UserId, Path.GetTempPath()),
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            NullLogger.Instance);

    private static ClientAttachmentUploadSource CreateSource(
        string fileName,
        string contentType,
        byte[] bytes) =>
        new(fileName, contentType, bytes.LongLength, _ =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    private static TrackingSource CreateTrackingSource(
        string fileName,
        string contentType,
        byte[] bytes)
    {
        var streams = new List<TrackingMemoryStream>();
        var source = new ClientAttachmentUploadSource(fileName, contentType, bytes.LongLength, _ =>
        {
            var stream = new TrackingMemoryStream(bytes);
            streams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        });
        return new TrackingSource(source, streams);
    }

    private static HttpResponseMessage Created(
        Guid attachmentId,
        ClientAttachmentUploadSource source)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new AttachmentDto(
                attachmentId,
                source.OriginalFileName,
                source.ContentType,
                source.Size,
                $"/api/attachments/{attachmentId:D}/download",
                ThumbnailUrl: null)),
        };
        response.Headers.Location = new Uri($"/api/attachments/{attachmentId:D}", UriKind.Relative);
        return response;
    }

    private sealed record TrackingSource(
        ClientAttachmentUploadSource Source,
        List<TrackingMemoryStream> OpenedStreams)
    {
        public int OpenCount => OpenedStreams.Count;
    }

    private sealed class FakeAuthenticationSession(
        string? accessToken,
        string? refreshedToken = null) : IClientAuthenticationSession
    {
        private string? currentAccessToken = accessToken;
        private int refreshCount;

        public int RefreshCount => Volatile.Read(ref refreshCount);

        public ValueTask<string?> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(currentAccessToken);

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref refreshCount);
            if (refreshedToken is null)
            {
                return Task.FromResult(false);
            }

            currentAccessToken = refreshedToken;
            return Task.FromResult(true);
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLengthStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => throw new IOException("length is unavailable");

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => Position;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
