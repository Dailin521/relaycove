using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RelayCove.Core;

namespace RelayCove.Zulip.Client.Tests;

public sealed class RealmMediaFailureTests
{
    private static readonly CredentialEnvelope Credentials = new(
        RealmEndpoint.Parse("https://chat.example.test"), "ada@example.test", 7, "test-only");

    [Theory]
    [InlineData("https://outside.example.test/user_uploads/7/image.png")]
    [InlineData("http://chat.example.test/user_uploads/7/image.png")]
    [InlineData("/unapproved/image.png")]
    public async Task GetRealmMediaAsync_WhenSourceIsNotAllowed_ReportsAddressReasonWithoutRequest(string url)
    {
        using var handler = new StubHandler();
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(
            new GetRealmMediaRequest(Credentials, new RealmMediaRequest(url, RealmMediaKind.Image, 1024))));

        Assert.Equal(GatewayErrorCode.MediaAddressNotAllowed, error.Code);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(url, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRealmMediaAsync_WhenTemporaryAddressLeavesRealm_ReportsAddressReasonWithoutFetchingIt()
    {
        using var handler = new StubHandler(Json("""{"url":"https://outside.example.test/user_uploads/temporary/7/image.png"}"""));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(ImageRequest()));

        Assert.Equal(GatewayErrorCode.MediaAddressNotAllowed, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public async Task GetRealmMediaAsync_WhenContentTypeIsUnsupported_ReportsFormatReason(string? contentType)
    {
        using var handler = new StubHandler(TemporaryUrl(), Binary([1, 2], contentType));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(ImageRequest()));

        Assert.Equal(GatewayErrorCode.UnsupportedImageType, error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRealmMediaAsync_WhenDeclaredOrStreamedBytesExceedLimit_ReportsSizeReason(bool declared)
    {
        using var payload = Binary([1, 2, 3, 4], "image/png");
        // A non-seekable stream omits Content-Length and exercises the streaming limit.
        if (!declared)
        {
            payload.Content.Dispose();
            payload.Content = new StreamContent(new NonSeekableStream([1, 2, 3, 4]));
            payload.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        }
        using var handler = new StubHandler(TemporaryUrl(), payload);
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(ImageRequest(3)));

        Assert.Equal(GatewayErrorCode.MediaTooLarge, error.Code);
    }

    [Theory]
    [InlineData(RealmMediaKind.Image)]
    [InlineData(RealmMediaKind.Avatar)]
    public async Task GetRealmMediaAsync_WhenImageIsEmpty_ReportsEmptyContentReason(RealmMediaKind kind)
    {
        using var handler = kind == RealmMediaKind.Image
            ? new StubHandler(TemporaryUrl(), Binary([], "image/png"))
            : new StubHandler(Binary([], "image/png"));
        using var gateway = new ZulipGateway(handler);
        var media = new RealmMediaRequest(
            kind == RealmMediaKind.Image ? "/user_uploads/7/image.png" : "/user_avatars/7/avatar.png", kind, 1024);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(new GetRealmMediaRequest(Credentials, media)));

        Assert.Equal(GatewayErrorCode.EmptyMediaContent, error.Code);
    }

    [Fact]
    public async Task GetRealmMediaAsync_WhenFileIsEmpty_AllowsEmptyFile()
    {
        using var handler = new StubHandler(TemporaryUrl(), Binary([], "application/octet-stream"));
        using var gateway = new ZulipGateway(handler);

        var result = await gateway.GetRealmMediaAsync(new GetRealmMediaRequest(
            Credentials, new RealmMediaRequest("/user_uploads/7/empty.txt", RealmMediaKind.File, 1024)));

        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task GetRealmMediaAsync_WhenResolutionResponseIsMalformed_ReportsResponseReason()
    {
        using var handler = new StubHandler(Json("not json"));
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(ImageRequest()));

        Assert.Equal(GatewayErrorCode.InvalidResponse, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, GatewayErrorCode.Unauthorized)]
    [InlineData(403, GatewayErrorCode.RequestFailed)]
    [InlineData(404, GatewayErrorCode.RequestFailed)]
    [InlineData(429, GatewayErrorCode.RateLimited)]
    [InlineData(503, GatewayErrorCode.ServerError)]
    [InlineData(302, GatewayErrorCode.RedirectNotAllowed)]
    public async Task GetRealmMediaAsync_WhenDownloadIsRejected_PreservesStatusWithoutRetry(int status, GatewayErrorCode code)
    {
        using var rejection = Json("""{"msg":"private server detail"}""");
        rejection.StatusCode = (HttpStatusCode)status;
        using var handler = new StubHandler(TemporaryUrl(), rejection);
        using var gateway = new ZulipGateway(handler);

        var error = await Assert.ThrowsAsync<GatewayException>(() => gateway.GetRealmMediaAsync(ImageRequest()));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(code, error.Code);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("private server detail", error.ToString(), StringComparison.Ordinal);
    }

    private static GetRealmMediaRequest ImageRequest(long maximumBytes = 1024) => new(
        Credentials, new RealmMediaRequest("/user_uploads/7/image.png", RealmMediaKind.Image, maximumBytes));

    private static HttpResponseMessage TemporaryUrl() => Json("""{"url":"/user_uploads/temporary/7/image.png"}""");

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Binary(byte[] bytes, string? contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (contentType is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Calls++]);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
