using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

public sealed class NotificationStateAdoptionTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AdoptNotificationStateAsync_WhenFirstRun_AdoptsOnlyPreVersionRows()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversation = CreateConversation();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        var oldCandidate = await cache.MergeIncomingMessageAsync(CreateMessage(1, conversation.Id));
        Assert.Equal(1, oldCandidate.NotificationCandidateMessageId);

        Assert.True(await cache.AdoptNotificationStateAsync());
        Assert.True(ReadNotificationHandled(identity, 1));
        Assert.Equal("1", ReadAppState(identity, "NotificationStateVersion"));

        var newCandidate = await cache.MergeIncomingMessageAsync(CreateMessage(2, conversation.Id));
        Assert.Equal(2, newCandidate.NotificationCandidateMessageId);
        Assert.False(ReadNotificationHandled(identity, 2));
        Assert.False(await cache.AdoptNotificationStateAsync());
        Assert.False(ReadNotificationHandled(identity, 2));

        await cache.DisposeAsync();
        await using var restarted = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.False(await restarted.AdoptNotificationStateAsync());
        Assert.False(ReadNotificationHandled(identity, 2));
    }

    [Fact]
    public async Task AdoptNotificationStateAsync_WhenCommitFails_RollsBackRowsAndVersion()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var faultInjector = new AdoptionCommitFaultInjector();
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            faultInjector);
        var conversation = CreateConversation();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        await cache.MergeIncomingMessageAsync(CreateMessage(1, conversation.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.AdoptNotificationStateAsync());

        Assert.False(ReadNotificationHandled(identity, 1));
        Assert.Null(ReadAppState(identity, "NotificationStateVersion"));
        Assert.True(await cache.AdoptNotificationStateAsync());
        Assert.True(ReadNotificationHandled(identity, 1));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static ConversationDto CreateConversation() => new(
        Guid.NewGuid(),
        ConversationType.PrivateChannel,
        "Conversation",
        null,
        DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
        LastMessageId: 0,
        LastReadMessageId: 0,
        UnreadCount: 0);

    private static MessageDto CreateMessage(long id, Guid conversationId) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        OtherUserId,
        "Sender",
        MessageType.Text,
        $"message {id}",
        null,
        Array.Empty<AttachmentDto>(),
        Array.Empty<Guid>(),
        DateTimeOffset.Parse("2026-08-03T03:00:00Z").AddSeconds(id));

    private static bool ReadNotificationHandled(
        AccountScopeIdentity identity,
        long messageId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsNotificationHandled
            FROM LocalMessages
            WHERE ServerMessageId = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static string? ReadAppState(AccountScopeIdentity identity, string key)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM LocalAppState WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static SqliteConnection OpenConnection(AccountScopeIdentity identity)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class AdoptionCommitFaultInjector : ILocalCacheFaultInjector
    {
        private int throwCommit = 1;

        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeNotificationAdoptionCommit()
        {
            if (Interlocked.Exchange(ref throwCommit, 0) != 0)
            {
                throw new InvalidOperationException("Injected adoption commit failure.");
            }
        }
    }
}
