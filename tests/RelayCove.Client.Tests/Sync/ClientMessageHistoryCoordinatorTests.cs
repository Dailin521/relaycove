using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientMessageHistoryCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.MessageHistory.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadHistoryAsync_WhenResponseIsValid_UsesExactKeysetAndMergesWithoutPreview()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 50, unreadCount: 0);
        var messages = new[]
        {
            CreateAttachmentMessage(10, prepared.Conversation.Id),
            CreateMessage(11, prepared.Conversation.Id),
        };
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal(
                $"/team/api/conversations/{prepared.Conversation.Id:D}/messages",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("beforeMessageId=20&limit=2", request.RequestUri.Query.TrimStart('?'));
            Assert.Equal(
                new AuthenticationHeaderValue("Bearer", "access-token"),
                request.Headers.Authorization);
            return Task.FromResult(Ok(new MessageHistoryResponse(
                messages,
                NextBeforeMessageId: 10,
                HasMore: true)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: 20,
            limit: 2);

        Assert.Equal(ClientMessageLoadStatus.Completed, outcome.Status);
        Assert.Equal([10L, 11L], outcome.Messages.Select(message => message.Id));
        Assert.Equal(10, outcome.NextBeforeMessageId);
        Assert.True(outcome.HasMore);
        var local = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Equal([10L, 11L], local.Messages.Select(message => message.Id));
        Assert.Equal(messages[0].Attachments, local.Messages[0].Attachments);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalAttachments;"));
        Assert.Equal(50, Scalar(
            prepared.Identity,
            "SELECT LastMessageId FROM LocalConversations;"));
        Assert.Equal(0, Scalar(
            prepared.Identity,
            "SELECT UnreadCount FROM LocalConversations;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadHistoryAsync_WhenPageInvariantIsInvalid_DoesNotPartiallyMerge(
        bool crossConversation)
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        var first = CreateMessage(1, prepared.Conversation.Id);
        var second = CreateMessage(2, prepared.Conversation.Id) with
        {
            ConversationId = crossConversation
                ? Guid.NewGuid()
                : prepared.Conversation.Id,
        };
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MessageHistoryResponse(
                [first, second],
                NextBeforeMessageId: crossConversation ? null : 2,
                HasMore: false)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageLoadStatus.ProtocolError, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenHasMorePageIsShort_RejectsWholeResponse()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MessageHistoryResponse(
                [CreateMessage(1, prepared.Conversation.Id)],
                NextBeforeMessageId: 1,
                HasMore: true)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 2);

        Assert.Equal(ClientMessageLoadStatus.ProtocolError, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenAttachmentProtocolIsInvalid_RejectsWholeResponse()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        var message = CreateAttachmentMessage(1, prepared.Conversation.Id);
        var attachment = Assert.Single(message.Attachments);
        message = message with
        {
            Attachments = [attachment with { DownloadUrl = "https://evil.example/file" }],
        };
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MessageHistoryResponse(
                [message],
                NextBeforeMessageId: null,
                HasMore: false)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageLoadStatus.ProtocolError, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalAttachments;"));
    }

    [Fact]
    public async Task LoadAroundAsync_WhenTargetIsValid_MergesBoundedWindowAndPreservesFlags()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 30, unreadCount: 0);
        var messages = new[]
        {
            CreateMessage(19, prepared.Conversation.Id),
            CreateAttachmentMessage(20, prepared.Conversation.Id),
            CreateMessage(21, prepared.Conversation.Id),
        };
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal(
                $"/team/api/conversations/{prepared.Conversation.Id:D}/messages/around/20",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("before=1&after=1", request.RequestUri.Query.TrimStart('?'));
            return Task.FromResult(Ok(new MessageAroundResponse(
                messages,
                TargetMessageId: 20,
                HasMoreBefore: true,
                HasMoreAfter: true)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadAroundAsync(
            prepared.Conversation.Id,
            messageId: 20,
            before: 1,
            after: 1);

        Assert.Equal(ClientMessageLoadStatus.Completed, outcome.Status);
        Assert.Equal(20, outcome.TargetMessageId);
        Assert.Equal([19L, 20L, 21L], outcome.Messages.Select(message => message.Id));
        Assert.Equal(messages[1].Attachments, outcome.Messages[1].Attachments);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalAttachments;"));
        Assert.True(outcome.HasMoreBefore);
        Assert.True(outcome.HasMoreAfter);
    }

    [Fact]
    public async Task LoadAroundAsync_WhenTargetIsMissing_RejectsWholeResponse()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 30, unreadCount: 0);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MessageAroundResponse(
                [CreateMessage(19, prepared.Conversation.Id)],
                TargetMessageId: 20,
                HasMoreBefore: false,
                HasMoreAfter: false)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadAroundAsync(
            prepared.Conversation.Id,
            messageId: 20,
            before: 1,
            after: 1);

        Assert.Equal(ClientMessageLoadStatus.ProtocolError, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LoadAroundAsync_WhenHasMoreSideIsShort_RejectsWholeResponse()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 30, unreadCount: 0);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MessageAroundResponse(
                [
                    CreateMessage(19, prepared.Conversation.Id),
                    CreateMessage(20, prepared.Conversation.Id),
                ],
                TargetMessageId: 20,
                HasMoreBefore: true,
                HasMoreAfter: false)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadAroundAsync(
            prepared.Conversation.Id,
            messageId: 20,
            before: 2,
            after: 1);

        Assert.Equal(ClientMessageLoadStatus.ProtocolError, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenStableRevocationReturned_PurgesAndNotifies()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        var revoked = new List<Guid>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    ApiErrorCodes.ConversationAccessRevoked,
                    "revoked",
                    TraceId: null)),
            })));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            (conversationId, _) =>
            {
                revoked.Add(conversationId);
                return Task.CompletedTask;
            });

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageLoadStatus.AccessRevoked, outcome.Status);
        Assert.Equal([prepared.Conversation.Id], revoked);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM RevokedConversations;"));
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenForbiddenIsNotStableRevocation_DoesNotPurge()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    "DifferentForbiddenCode",
                    "denied",
                    TraceId: null)),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageLoadStatus.AccessDenied, outcome.Status);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM RevokedConversations;"));
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenUnauthorized_RefreshesOnceAndRetriesSameUri()
    {
        var prepared = await CreatePreparedAsync(lastMessageId: 0, unreadCount: 0);
        var requestUris = new List<Uri>();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            requestUris.Add(request.RequestUri!);
            requestCount++;
            return Task.FromResult(requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Ok(new MessageHistoryResponse(
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMore: false)));
        }));
        var authentication = new FakeAuthenticationSession(
            "expired-token",
            "refreshed-token");
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            authenticationSession: authentication);

        var outcome = await coordinator.LoadHistoryAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageLoadStatus.Completed, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, requestUris.Count);
        Assert.Equal(requestUris[0], requestUris[1]);
    }

    [Fact]
    public void MessageHistoryOutcomeToString_RedactsPayloadAndIdentifiers()
    {
        const string secret = "message-secret-content";
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(7, conversationId) with { Content = secret };
        object[] outcomes =
        [
            new LocalMessagePageReadOutcome(
                LocalCacheOperationStatus.Ready,
                conversationId,
                [message],
                NextBeforeMessageId: 7,
                HasMoreBefore: true),
            new ClientMessageHistoryPageOutcome(
                ClientMessageLoadStatus.Completed,
                [message],
                NextBeforeMessageId: 7,
                HasMore: true),
            new ClientMessageAroundOutcome(
                ClientMessageLoadStatus.Completed,
                [message],
                TargetMessageId: 7,
                HasMoreBefore: true,
                HasMoreAfter: true),
            ClientMessageHistoryHttpResult<MessageHistoryResponse>.Success(
                new MessageHistoryResponse([message], 7, HasMore: true)),
        ];

        foreach (var outcome in outcomes)
        {
            var text = outcome.ToString()!;
            Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(conversationId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedHistory> CreatePreparedAsync(
        long lastMessageId,
        int unreadCount)
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Conversation",
            AvatarUrl: null,
            CreatedAt: DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
            lastMessageId,
            LastReadMessageId: 0,
            unreadCount);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        return new PreparedHistory(identity, cache, conversation);
    }

    private static ClientMessageHistoryCoordinator CreateCoordinator(
        PreparedHistory prepared,
        HttpClient httpClient,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        IClientAuthenticationSession? authenticationSession = null) =>
        new(
            prepared.Identity,
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            prepared.Cache,
            NullLogger<ClientMessageHistoryCoordinator>.Instance,
            conversationRevokedAsync);

    private static MessageDto CreateMessage(long id, Guid conversationId) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        OtherUserId,
        "Sender",
        MessageType.Text,
        $"message {id}",
        ReplyToMessageId: null,
        Array.Empty<AttachmentDto>(),
        Array.Empty<Guid>(),
        DateTimeOffset.Parse("2026-08-03T03:00:00Z").AddSeconds(id));

    private static MessageDto CreateAttachmentMessage(long id, Guid conversationId)
    {
        var attachmentId = Guid.Parse($"{id:x8}-1111-2222-3333-444444444444");
        return CreateMessage(id, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments =
            [
                new AttachmentDto(
                    attachmentId,
                    $"history-{id}.pdf",
                    "application/pdf",
                    4096,
                    $"/api/attachments/{attachmentId:D}/download",
                    ThumbnailUrl: null),
            ],
        };
    }

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed record PreparedHistory(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation);

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
}
