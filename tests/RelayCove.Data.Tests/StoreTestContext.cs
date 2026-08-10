using Microsoft.Data.Sqlite;
using RelayCove.Core;
using RelayCove.Data;

namespace RelayCove.Data.Tests;

internal sealed class StoreTestContext : IAsyncDisposable
{
    private StoreTestContext(string root, SqliteAccountStore store)
    {
        Root = root;
        Store = store;
    }

    public string Root { get; }
    public SqliteAccountStore Store { get; }

    public static StoreTestContext Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelayCove.Data.Tests", Guid.NewGuid().ToString("N"));
        return new StoreTestContext(root, new SqliteAccountStore(root));
    }

    public string DatabasePath(AccountId accountId) => Path.Combine(Root, "accounts", accountId.Value, "relaycove.db");

    public SqliteConnection Open(AccountId accountId)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath(accountId)};Pooling=False");
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal static class StoreTestData
{
    public static StoredAccount Account(string realm = "https://one.example/", long userId = 10)
    {
        var endpoint = RealmEndpoint.Parse(realm);
        return new StoredAccount(AccountId.Create(endpoint, userId), endpoint, $"user{userId}@example.test", userId);
    }

    public static ChatMessage Message(long id, ConversationKey conversation, bool isRead = false, string? content = null) =>
        new(id, conversation, 10, content ?? $"message-{id}", DateTimeOffset.UnixEpoch.AddSeconds(id), isRead, "Sender");

    public static RegisterResult Register(
        IReadOnlyList<Subscription> subscriptions,
        IReadOnlyList<DomainEvent>? events = null,
        IReadOnlyList<ConversationKey>? recent = null,
        UnreadState? unread = null,
        IReadOnlyList<UserProfile>? users = null) =>
        new(
            "ephemeral-queue",
            100,
            TimeSpan.FromSeconds(30),
            10_000,
            200,
            subscriptions,
            users ?? [new UserProfile(10, "Sender", "sender@example.test")],
            recent ?? [],
            unread ?? new UnreadState(),
            events ?? []);
}
