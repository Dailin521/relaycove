using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RelayCove.Server.Data;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Admin;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AdminOperationsEndpointTests(
    RelayCoveWebApplicationFactory factory) : IClassFixture<RelayCoveWebApplicationFactory>, IAsyncLifetime
{
    private const string Password = "a secure administrator operations test phrase";

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ChannelOperations_WhenCurrentAdministrator_RenameDeleteAndExcludeDirects()
    {
        var adminName = CreateUserName("ops-admin");
        var memberName = CreateUserName("ops-member");
        var observerName = CreateUserName("ops-observer");
        await factory.CreateUserAsync(adminName, Password, isAdmin: true);
        var memberId = await factory.CreateUserAsync(memberName, Password);
        await factory.CreateUserAsync(observerName, Password);
        using var adminClient = await CreateAuthenticatedClientAsync(adminName);
        using var memberClient = await CreateAuthenticatedClientAsync(memberName);
        using var observerClient = await CreateAuthenticatedClientAsync(observerName);

        var publicChannel = await CreateChannelAsync(adminClient, ConversationType.PublicChannel, "Public operations");
        var privateChannel = await CreateChannelAsync(adminClient, ConversationType.PrivateChannel, "Private operations");
        using (var regularUpdate = await observerClient.PutAsJsonAsync(
                   $"/api/conversations/{publicChannel.Id:D}",
                   new UpdateConversationRequest("Unauthorized rename")))
        {
            await AssertErrorAsync(regularUpdate, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
        }

        using (var regularDelete = await observerClient.DeleteAsync($"/api/conversations/{publicChannel.Id:D}"))
        {
            await AssertErrorAsync(regularDelete, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
        }

        using (var memberResponse = await adminClient.PostAsJsonAsync(
                   $"/api/conversations/{privateChannel.Id:D}/members",
                   new UpsertConversationMemberRequest(memberId, ConversationMemberRole.Member)))
        {
            Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
        }

        using (var directResponse = await adminClient.PostAsJsonAsync(
                   "/api/conversations",
                   new CreateConversationRequest(ConversationType.Direct, ParticipantUserId: memberId)))
        {
            Assert.Equal(HttpStatusCode.Created, directResponse.StatusCode);
            var direct = (await directResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
            using var directUpdate = await adminClient.PutAsJsonAsync(
                $"/api/conversations/{direct.Id:D}",
                new UpdateConversationRequest("Nope"));
            await AssertErrorAsync(directUpdate, HttpStatusCode.Conflict, ApiErrorCodes.ConversationTypeConflict);
        }

        using (var listResponse = await adminClient.GetAsync("/api/admin/channels"))
        {
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var channels = (await listResponse.Content.ReadFromJsonAsync<AdminChannelResponse[]>())!;
            Assert.Contains(channels, channel => channel.Id == publicChannel.Id);
            Assert.Contains(channels, channel => channel.Id == privateChannel.Id);
            Assert.DoesNotContain(channels, channel => channel.Type == ConversationType.Direct);
        }

        using (var updateResponse = await adminClient.PutAsJsonAsync(
                   $"/api/conversations/{privateChannel.Id:D}",
                   new UpdateConversationRequest("Private renamed")))
        {
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Equal("Private renamed", (await updateResponse.Content.ReadFromJsonAsync<ConversationDto>())!.Name);
        }

        using (var deleteResponse = await adminClient.DeleteAsync($"/api/conversations/{privateChannel.Id:D}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }

        using (var revokedResponse = await memberClient.GetAsync($"/api/conversations/{privateChannel.Id:D}"))
        {
            await AssertErrorAsync(revokedResponse, HttpStatusCode.Forbidden, ApiErrorCodes.ConversationAccessRevoked);
        }

        using (var regularAdminRoute = await observerClient.GetAsync("/api/admin/channels"))
        {
            await AssertErrorAsync(regularAdminRoute, HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.True((await dbContext.Conversations.FindAsync(privateChannel.Id))!.IsDeleted);
    }

    [Fact]
    public async Task UploadSettingsAndStatus_WhenAdministrator_PersistLimitWithoutSensitiveStorageDetails()
    {
        var adminName = CreateUserName("status-admin");
        await factory.CreateUserAsync(adminName, Password, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        const long limit = 2L * 1024 * 1024;

        using (var invalid = await client.PutAsJsonAsync(
                   "/api/admin/settings/upload",
                   new UpdateUploadSettingsRequest((1L * 1024 * 1024) - 1)))
        {
            await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed);
        }

        using (var update = await client.PutAsJsonAsync(
                   "/api/admin/settings/upload",
                   new UpdateUploadSettingsRequest(limit)))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(limit, (await update.Content.ReadFromJsonAsync<UploadSettingsResponse>())!.EffectiveMaximumFileBytes);
        }

        using (var get = await client.GetAsync("/api/admin/settings/upload"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(limit, (await get.Content.ReadFromJsonAsync<UploadSettingsResponse>())!.EffectiveMaximumFileBytes);
        }

        using var status = await client.GetAsync("/api/admin/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var response = (await status.Content.ReadFromJsonAsync<ServerStatusResponse>())!;
        Assert.Equal(limit, response.EffectiveUploadLimitBytes);
        Assert.True(response.UptimeSeconds >= 0);
        Assert.True(response.OnlineConnectionCount >= 0);
        Assert.DoesNotContain(factory.DatabasePath, await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.UploadsPath, await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSettings_WhenStreamingBodyExceedsPersistedSnapshot_ReturnsStable413()
    {
        var adminName = CreateUserName("stream-limit-admin");
        await factory.CreateUserAsync(adminName, Password, isAdmin: true);
        using var client = await CreateAuthenticatedClientAsync(adminName);
        const long limit = 1L * 1024 * 1024;
        using (var update = await client.PutAsJsonAsync(
                   "/api/admin/settings/upload",
                   new UpdateUploadSettingsRequest(limit)))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        await using var source = new NonSeekableReadStream(new MemoryStream(new byte[limit + 1]));
        using var file = new StreamContent(source);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(file, "file", "too-large.bin");
        using var response = await client.PostAsync("/api/attachments", multipart);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.AttachmentTooLarge);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, Password, "admin-operations-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<ConversationDto> CreateChannelAsync(
        HttpClient client,
        ConversationType type,
        string name)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(type, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    private static string CreateUserName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
