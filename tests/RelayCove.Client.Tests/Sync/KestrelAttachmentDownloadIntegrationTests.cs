using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Attachments;
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

    [Fact]
    public async Task DownloadImageAsync_ThroughRealKestrel_TwoIndependentAccountsReceiveExactPayloadAndSafePreviews()
    {
        var payload = await RunOnStaAsync(() => Task.FromResult(CreatePng(640, 320)));
        using var factory = new RelayCoveWebApplicationFactory();
        factory.UseKestrel(port: 0);
        await factory.InitializeDatabaseAsync();
        var aliceName = $"kestrel-image-alice-{Guid.NewGuid():N}";
        var bobName = $"kestrel-image-bob-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(aliceName, Password, isAdmin: true);
        await factory.CreateUserAsync(bobName, Password);

        using var aliceHttpClient = CreateKestrelClient(factory);
        using var bobHttpClient = CreateKestrelClient(factory);
        var aliceLogin = await LoginAsync(aliceHttpClient, aliceName);
        var bobLogin = await LoginAsync(bobHttpClient, bobName);
        Assert.NotEqual(aliceLogin.UserId, bobLogin.UserId);

        var conversation = await CreateConversationAsync(aliceHttpClient, aliceLogin.AccessToken);
        var uploaded = await UploadAsync(
            aliceHttpClient,
            aliceLogin.AccessToken,
            payload,
            "direct-preview.png",
            "image/png");
        var sent = await SendAsync(
            aliceHttpClient,
            aliceLogin.AccessToken,
            conversation.Id,
            uploaded.Id,
            MessageType.Image);
        var sentAttachment = Assert.Single(sent.Attachments);
        Assert.Equal("image/png", sentAttachment.ContentType);

        // The sender also passes through the received-message cache, download, and constrained
        // thumbnail path rather than relying on the composer draft preview alone.
        var aliceIdentity = AccountScopeIdentity.Create(
            Assert.IsType<Uri>(aliceHttpClient.BaseAddress),
            aliceLogin.UserId,
            clientRoot);
        var alicePreview = await DownloadAndLoadThumbnailAsync(
            aliceIdentity,
            aliceHttpClient,
            aliceLogin.AccessToken,
            conversation,
            sent,
            payload);

        var bobConversation = await GetConversationAsync(
            bobHttpClient,
            bobLogin.AccessToken,
            conversation.Id);
        var bobHistory = await GetHistoryAsync(
            bobHttpClient,
            bobLogin.AccessToken,
            conversation.Id);
        var received = Assert.Single(bobHistory.Messages);
        var receivedAttachment = Assert.Single(received.Attachments);
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal(MessageType.Image, received.Type);
        Assert.Equal(sentAttachment, receivedAttachment);

        var bobIdentity = AccountScopeIdentity.Create(
            Assert.IsType<Uri>(bobHttpClient.BaseAddress),
            bobLogin.UserId,
            clientRoot);
        Assert.NotEqual(aliceIdentity.Id, bobIdentity.Id);
        var bobPreview = await DownloadAndLoadThumbnailAsync(
            bobIdentity,
            bobHttpClient,
            bobLogin.AccessToken,
            bobConversation,
            received,
            payload);

        Assert.Equal(new ClientAttachmentImageSafeSize(320, 160), alicePreview.SafeSize);
        Assert.Equal(new ClientAttachmentImageSafeSize(320, 160), bobPreview.SafeSize);
        Assert.NotSame(alicePreview.Image, bobPreview.Image);
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
        byte[] payload,
        string fileName = "kestrel.bin",
        string contentType = "application/octet-stream")
    {
        using var form = new MultipartFormDataContent($"relaycove-{Guid.NewGuid():N}");
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
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
        Guid attachmentId,
        MessageType messageType = MessageType.File)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest(
                Guid.NewGuid(),
                conversationId,
                messageType,
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

    private static HttpClient CreateKestrelClient(RelayCoveWebApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.BaseAddress = GetKestrelBaseAddress(factory);
        return client;
    }

    private static async Task<ConversationDto> GetConversationAsync(
        HttpClient client,
        string accessToken,
        Guid conversationId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/conversations/{conversationId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<MessageHistoryResponse> GetHistoryAsync(
        HttpClient client,
        string accessToken,
        Guid conversationId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/conversations/{conversationId:D}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageHistoryResponse>())!;
    }

    private async Task<ClientAttachmentImageLoadOutcome> DownloadAndLoadThumbnailAsync(
        AccountScopeIdentity identity,
        HttpClient client,
        string accessToken,
        ConversationDto conversation,
        MessageDto message,
        byte[] expectedPayload)
    {
        var attachment = Assert.Single(message.Attachments);
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
                client,
                new FixedAuthenticationSession(accessToken),
                NullLogger.Instance),
            NullLogger<ClientAttachmentDownloadCoordinator>.Instance);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());

        var downloaded = await coordinator.DownloadAsync(conversation.Id, attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, downloaded.Status);
        Assert.NotNull(downloaded.LocalPath);
        Assert.Equal(
            expectedPayload,
            await File.ReadAllBytesAsync(
                Path.Combine(cacheStore.ScopeDirectory, downloaded.LocalPath!)));
        Assert.Equal(2, ReadDownloadStatus(identity));
        return await RunOnStaAsync(
            () => coordinator.LoadImageAsync(
                conversation.Id,
                attachment.Id,
                ClientAttachmentImageRendition.Thumbnail,
                () => ClientAttachmentImageLoadStatus.Ready));
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

    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 22;
            pixels[index + 1] = 119;
            pixels[index + 2] = 210;
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
