using Microsoft.Data.Sqlite;
using RelayCove.Core;
using RelayCove.Data;

namespace RelayCove.Data.Tests;

public sealed class SqliteAccountStoreSchemaTests
{
    [Fact]
    public async Task NativeSqlite_WhenLoaded_IsAtLeastPatchedVersion()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        await context.Store.InitializeAsync(account);
        await using var connection = context.Open(account.AccountId);

        var version = Version.Parse(await ScalarStringAsync(connection, "SELECT sqlite_version();"));

        Assert.True(version >= new Version(3, 50, 2), $"Loaded SQLite {version} is below the security floor 3.50.2.");
    }

    [Fact]
    public async Task InitializeAsync_WhenAccountIsNew_CreatesExpectedWalDatabaseAndSchema()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();

        await context.Store.InitializeAsync(account);

        Assert.Matches("^[0-9a-f]{64}$", account.AccountId.Value);
        Assert.True(File.Exists(context.DatabasePath(account.AccountId)));
        await using var connection = context.Open(account.AccountId);
        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        var tables = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type='table';");
        Assert.Contains("account_metadata", tables);
        Assert.Contains("users", tables);
        Assert.Contains("subscriptions", tables);
        Assert.Contains("topics", tables);
        Assert.Contains("recent_dm", tables);
        Assert.Contains("messages", tables);
        Assert.Contains("message_reactions", tables);
        Assert.Contains("unread_counts", tables);
        Assert.Contains("unread_state", tables);
        Assert.Contains("schema_info", tables);
    }

    [Fact]
    public async Task MigrateAsync_WhenSchemaVersionIsCurrent_PreservesMetadataAndVersion()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        await context.Store.InitializeAsync(account);
        await using (var connection = context.Open(account.AccountId))
        {
            await ExecuteAsync(connection, $"UPDATE schema_info SET version = {SqliteAccountStore.CurrentSchemaVersion};");
        }

        await context.Store.MigrateAsync(account.AccountId);

        var loaded = await context.Store.LoadAsync(account.AccountId);
        Assert.Equal(account, loaded!.Account);
        await using var verify = context.Open(account.AccountId);
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "SELECT version FROM schema_info;"));
    }

    [Fact]
    public async Task MigrateAsync_WhenSchemaIsVersionOne_PreservesRowsAndAddsMessageCapabilities()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new DirectMessage([20]);
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [],
            [new MessageUpsertEvent(StoreTestData.Message(9, conversation, content: "preserve-me"))],
            users: [new UserProfile(20, "Bea")]));
        await using (var connection = context.Open(account.AccountId))
        {
            await ExecuteAsync(connection, """
                DROP TABLE message_reactions;
                ALTER TABLE messages DROP COLUMN is_starred;
                ALTER TABLE messages DROP COLUMN sender_avatar_url;
                ALTER TABLE users DROP COLUMN is_bot;
                ALTER TABLE users DROP COLUMN avatar_version;
                ALTER TABLE users DROP COLUMN avatar_url;
                UPDATE schema_info SET version = 1;
                PRAGMA user_version = 1;
                """);
        }

        await context.Store.MigrateAsync(account.AccountId);

        var loaded = Assert.IsType<AccountSnapshot>(await context.Store.LoadAsync(account.AccountId));
        var message = Assert.Single((await context.Store.QueryMessagePageAsync(account.AccountId, conversation, null, 20)).Messages);
        Assert.Equal("preserve-me", message.Content);
        Assert.False(message.IsStarred);
        Assert.Empty(message.Reactions);
        Assert.Equal("Bea", loaded.State.Users[20].FullName);
        await using var verify = context.Open(account.AccountId);
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "SELECT version FROM schema_info;"));
        Assert.Equal(1, await ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='message_reactions';"));
    }

    [Fact]
    public async Task MigrateAsync_WhenSchemaIsVersionTwo_PreservesMessagesAndAddsReactionLookupIndex()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new DirectMessage([20]);
        await context.Store.InitializeAsync(account);
        await context.Store.StoreMessagePageAsync(account.AccountId, [StoreTestData.Message(9, conversation)]);
        await using (var connection = context.Open(account.AccountId))
        {
            await ExecuteAsync(connection, """
                DROP INDEX ix_message_reactions_message_id;
                UPDATE schema_info SET version = 2;
                PRAGMA user_version = 2;
                """);
        }

        await context.Store.MigrateAsync(account.AccountId);

        Assert.Equal(9, Assert.Single((await context.Store.QueryMessagePageAsync(account.AccountId, conversation, null, 20)).Messages).Id);
        await using var verify = context.Open(account.AccountId);
        Assert.Equal(1, await ScalarLongAsync(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_message_reactions_message_id';"));
        Assert.Equal(3, await ScalarLongAsync(verify, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task ListAsync_WhenMultipleAccountsExist_ReturnsEachIsolatedAccount()
    {
        await using var context = StoreTestContext.Create();
        var one = StoreTestData.Account("https://one.example/", 10);
        var two = StoreTestData.Account("https://two.example/", 20);
        await context.Store.InitializeAsync(one);
        await context.Store.InitializeAsync(two);

        var accounts = await context.Store.ListAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Contains(one, accounts);
        Assert.Contains(two, accounts);
        Assert.NotEqual(context.DatabasePath(one.AccountId), context.DatabasePath(two.AccountId));
    }

    [Fact]
    public async Task InitializeAsync_WhenApiKeyExistsInMemory_DoesNotPersistItAnywhere()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var secret = $"secret-{Guid.NewGuid():N}";
        _ = new RelayCove.Core.CredentialEnvelope(account.Realm, account.Email, account.UserId, secret);

        await context.Store.InitializeAsync(account);

        var accountDirectory = Path.GetDirectoryName(context.DatabasePath(account.AccountId))!;
        foreach (var file in Directory.EnumerateFiles(accountDirectory))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}
