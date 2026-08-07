using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

public sealed class ClientAttachmentDownloadHttpTransportTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");

    [Fact]
    public async Task DownloadAsync_WhenResponseIsFullyVerified_WritesStagingAndReturnsHash()
    {
        var payload = "download payload"u8.ToArray();
        var attachment = CreateAttachment(payload, "image/png");
        var progress = new List<ClientAttachmentDownloadProgress>();
        using var staging = new MemoryStream();
        staging.WriteByte(99);
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                new Uri(ServerBaseUri, attachment.DownloadUrl.TrimStart('/')),
                request.RequestUri);
            Assert.Equal("access-token", request.Headers.Authorization!.Parameter);
            Assert.Null(request.Headers.Range);
            return Task.FromResult(Ok(payload, attachment, "image/png"));
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None,
            progress.Add);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.Success, result.Status);
        Assert.Equal(Sha256(payload), result.Sha256);
        Assert.Equal(payload.LongLength, result.TotalBytes);
        Assert.Equal(payload, staging.ToArray());
        var reported = Assert.Single(progress);
        Assert.Equal(payload.LongLength, reported.BytesWritten);
        Assert.Equal(payload.LongLength, reported.TotalBytes);
        Assert.Equal(100, reported.Percent);
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("uppercase")]
    [InlineData("unquoted")]
    public async Task DownloadAsync_WhenEtagIsNotQuotedLowercaseStrongSha256_RejectsResponse(string variant)
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload);
        var hash = Sha256(payload);
        var tag = variant switch
        {
            "weak" => new EntityTagHeaderValue($"\"{hash}\"", isWeak: true),
            "uppercase" => new EntityTagHeaderValue($"\"{hash.ToUpperInvariant()}\""),
            _ => null,
        };
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(payload, attachment);
            if (tag is not null)
            {
                response.Headers.ETag = tag;
            }
            else
            {
                Assert.True(response.Headers.TryAddWithoutValidation("ETag", hash));
            }
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.Null(result.Sha256);
        Assert.Empty(staging.ToArray());
    }

    [Theory]
    [InlineData("content-length")]
    [InlineData("content-type")]
    [InlineData("content-encoding")]
    [InlineData("content-range")]
    public async Task DownloadAsync_WhenMetadataResponseHeadersConflict_RejectsBeforeWriting(string variant)
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload, "application/octet-stream");
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(payload, attachment);
            if (variant == "content-length")
            {
                response.Content.Headers.ContentLength = payload.Length + 1;
            }
            else if (variant == "content-type")
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            }
            else if (variant == "content-encoding")
            {
                response.Content.Headers.ContentEncoding.Add("gzip");
            }
            else
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    0,
                    payload.Length - 1,
                    payload.Length);
            }

            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.Empty(staging.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_WhenEffectiveResponseUriChanged_RejectsRedirectedContent()
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload);
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(payload, attachment);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://redirected.example/attachment");
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.Empty(staging.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_WhenContentTypeIsAbsent_DoesNotUseItToChooseThePath()
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload, "application/octet-stream");
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(payload, attachment);
            response.Content.Headers.ContentType = null;
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.Success, result.Status);
        Assert.Equal(payload, staging.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public async Task DownloadAsync_WhenActualStreamLengthIsNotMetadataSize_RejectsResponse(byte[] responsePayload)
    {
        var attachment = CreateAttachment([1, 2, 3]);
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var content = new UnknownLengthContent(responsePayload, "application/octet-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue($"\"{Sha256(responsePayload)}\"");
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.NotEqual(ClientAttachmentDownloadHttpStatus.Success, result.Status);
    }

    [Fact]
    public async Task DownloadAsync_WhenActualHashDoesNotMatchEtag_RejectsResponse()
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload);
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(payload, attachment);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{new string('0', 64)}\"");
            return Task.FromResult(response);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(payload, staging.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_WhenStable401_RefreshesOnceResetsStagingAndReplays()
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload);
        var authentication = new FakeAuthenticationSession("old-token", "new-token");
        var tokens = new List<string?>();
        var requests = 0;
        using var staging = new MemoryStream();
        staging.WriteByte(42);
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            tokens.Add(request.Headers.Authorization!.Parameter);
            return Task.FromResult(Interlocked.Increment(ref requests) == 1
                ? Error(HttpStatusCode.Unauthorized, ApiErrorCodes.AuthenticationRequired)
                : Ok(payload, attachment));
        }));

        var result = await CreateTransport(httpClient, authentication).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.Success, result.Status);
        Assert.Equal(["old-token", "new-token"], tokens);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(payload, staging.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_When401IsNotStable_DoesNotRefreshOrReplay()
    {
        var attachment = CreateAttachment([1]);
        var authentication = new FakeAuthenticationSession("access-token", "new-token");
        var requests = 0;
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("<html>gateway</html>", Encoding.UTF8, "text/html"),
            });
        }));

        var result = await CreateTransport(httpClient, authentication).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.ProtocolError, result.Status);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Equal(0, authentication.RefreshCount);
    }

    [Theory]
    [InlineData(ApiErrorCodes.ConversationAccessRevoked, (int)ClientAttachmentDownloadHttpStatus.AccessRevoked)]
    [InlineData("OtherForbidden", (int)ClientAttachmentDownloadHttpStatus.AccessDenied)]
    public async Task DownloadAsync_WhenForbidden_UsesStableRevocationClassification(
        string errorCode,
        int expectedStatusValue)
    {
        var attachment = CreateAttachment([1]);
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Error(HttpStatusCode.Forbidden, errorCode))));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal((ClientAttachmentDownloadHttpStatus)expectedStatusValue, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent, (int)ClientAttachmentDownloadHttpStatus.ProtocolError)]
    [InlineData(HttpStatusCode.Found, (int)ClientAttachmentDownloadHttpStatus.RemoteFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, (int)ClientAttachmentDownloadHttpStatus.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, (int)ClientAttachmentDownloadHttpStatus.TransientFailure)]
    public async Task DownloadAsync_WhenNon200Response_DoesNotReplay(
        HttpStatusCode statusCode,
        int expectedStatusValue)
    {
        var attachment = CreateAttachment([1]);
        var requests = 0;
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None);

        Assert.Equal((ClientAttachmentDownloadHttpStatus)expectedStatusValue, result.Status);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task DownloadAsync_WhenProgressCallbackThrows_IsolatesTheFailure()
    {
        var payload = new byte[] { 1, 2, 3 };
        var attachment = CreateAttachment(payload);
        var reported = new List<long>();
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(payload, attachment))));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            CancellationToken.None,
            progress =>
            {
                reported.Add(progress.BytesWritten);
                throw new InvalidOperationException("receiver failure");
            });

        Assert.Equal(ClientAttachmentDownloadHttpStatus.Success, result.Status);
        Assert.Equal([payload.LongLength], reported);
    }

    [Fact]
    public async Task DownloadAsync_WhenCallerCancelsInFlight_ReturnsCanceledWithoutRetry()
    {
        var attachment = CreateAttachment([1]);
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var staging = new MemoryStream();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, token) =>
        {
            Interlocked.Increment(ref requests);
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        }));

        var result = await CreateTransport(httpClient).DownloadAsync(
            attachment,
            staging,
            cancellation.Token);

        Assert.Equal(ClientAttachmentDownloadHttpStatus.Canceled, result.Status);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public void DownloadResult_ToString_RedactsIntegrityValues()
    {
        var hash = new string('a', 64);
        var result = ClientAttachmentDownloadHttpResult.Success(hash, 10);

        Assert.DoesNotContain(hash, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("10", result.ToString(), StringComparison.Ordinal);
    }

    private static ClientAttachmentDownloadHttpTransport CreateTransport(
        HttpClient httpClient,
        IClientAuthenticationSession? authenticationSession = null) =>
        new(
            AccountScopeIdentity.Create(ServerBaseUri, UserId, Path.GetTempPath()),
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            NullLogger.Instance);

    private static AttachmentDto CreateAttachment(byte[] payload, string contentType = "application/octet-stream")
    {
        var id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        return new AttachmentDto(
            id,
            "attachment.bin",
            contentType,
            payload.LongLength,
            $"/api/attachments/{id:D}/download",
            ThumbnailUrl: null);
    }

    private static HttpResponseMessage Ok(
        byte[] payload,
        AttachmentDto attachment,
        string? contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(payload);
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.ETag = new EntityTagHeaderValue($"\"{Sha256(payload)}\"");
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string code) =>
        new(statusCode)
        {
            Content = JsonContent.Create(new ApiErrorResponse(code, "A stable error occurred.")),
        };

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

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

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] payload;

        public UnknownLengthContent(byte[] payload, string contentType)
        {
            this.payload = payload;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
