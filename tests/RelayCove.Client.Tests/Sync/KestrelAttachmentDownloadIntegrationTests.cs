using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

[Collection(SqliteTestCollection.Name)]
public sealed class KestrelAttachmentDownloadIntegrationTests : IDisposable
{
    private const string Password = "a secure kestrel attachment phrase";
    private readonly string clientRoot = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.KestrelAttachment.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadAsync_ThroughRealKestrel_PersistsVerifiedDiskAndSqliteState()
    {
        var payload = "real kestrel attachment payload"u8.ToArray();
        using var factory = new RelayCoveWebApplicationFactory();
        factory.UseKestrel(port: 0);
        await factory.InitializeDatabaseAsync();
        var userName = $"kestrel-attachment-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password, isAdmin: true);
        using var httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        httpClient.BaseAddress = GetKestrelBaseAddress(factory);
        var login = await LoginAsync(httpClient, userName);
        var conversation = await CreateConversationAsync(httpClient, login.AccessToken);
        var uploaded = await UploadAsync(httpClient, login.AccessToken, payload);
        var message = await SendAsync(
            httpClient,
            login.AccessToken,
            conversation.Id,
            uploaded.Id);
        var attachment = Assert.Single(message.Attachments);
        var identity = AccountScopeIdentity.Create(
            Assert.IsType<Uri>(httpClient.BaseAddress),
            login.UserId,
            clientRoot);
        await using var localCache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await localCache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await localCache.MergeIncomingMessageAsync(message)).Result);
        var cacheStore = new ClientAttachmentCacheStore(
            identity,
            Path.Combine(clientRoot, "cache"));
        await using var coordinator = new ClientAttachmentDownloadCoordinator(
            localCache,
            cacheStore,
            new ClientAttachmentDownloadHttpTransport(
                identity,
                httpClient,
                new FixedAuthenticationSession(login.AccessToken),
                NullLogger.Instance),
            NullLogger<ClientAttachmentDownloadCoordinator>.Instance);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());

        var outcome = await coordinator.DownloadAsync(conversation.Id, attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.LocalPath);
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(
                Path.Combine(cacheStore.ScopeDirectory, outcome.LocalPath!)));
        Assert.Equal(2, ReadDownloadStatus(identity));
        Assert.True(Directory.EnumerateFiles(factory.UploadsPath).Any());

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        anonymous.BaseAddress = GetKestrelBaseAddress(factory);
        using var denied = await anonymous.GetAsync(attachment.DownloadUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Null(denied.Headers.ETag);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(clientRoot))
        {
            Directory.Delete(clientRoot, recursive: true);
        }
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(userName, Password, "kestrel-integration", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static async Task<ConversationDto> CreateConversationAsync(
        HttpClient client,
        string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations")
        {
            Content = JsonContent.Create(new CreateConversationRequest(
                ConversationType.PublicChannel,
                "Kestrel attachment integration")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<AttachmentDto> UploadAsync(
        HttpClient client,
        string accessToken,
        byte[] payload)
    {
        using var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(file, "file", "kestrel.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/attachments")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
    }

    private static async Task<MessageDto> SendAsync(
        HttpClient client,
        string accessToken,
        Guid conversationId,
        Guid attachmentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                MessageType.File,
                "kestrel attachment",
                ReplyToMessageId: null,
                AttachmentIds: [attachmentId],
                MentionUserIds: [])),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>())!;
    }

    private static long ReadDownloadStatus(AccountScopeIdentity identity)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DownloadStatus FROM LocalAttachments;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static Uri GetKestrelBaseAddress(RelayCoveWebApplicationFactory factory)
    {
        var server = factory.Services.GetRequiredService<IServer>();
        var address = Assert.Single(
            server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return new Uri(address, UriKind.Absolute);
    }

    private sealed class FixedAuthenticationSession(string accessToken) :
        IClientAuthenticationSession
    {
        public ValueTask<string?> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(accessToken);

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
