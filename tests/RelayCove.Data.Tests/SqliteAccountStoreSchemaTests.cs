using Microsoft.Data.Sqlite;
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
        Assert.Contains("unread_counts", tables);
        Assert.Contains("unread_state", tables);
        Assert.Contains("schema_info", tables);
    }

    [Fact]
    public async Task MigrateAsync_WhenSchemaVersionIsOld_AdvancesVersionWithoutLosingMetadata()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        await context.Store.InitializeAsync(account);
        await using (var connection = context.Open(account.AccountId))
        {
            await ExecuteAsync(connection, "PRAGMA user_version = 0; DELETE FROM schema_info;");
        }

        await context.Store.MigrateAsync(account.AccountId);

        var loaded = await context.Store.LoadAsync(account.AccountId);
        Assert.Equal(account, loaded!.Account);
        await using var verify = context.Open(account.AccountId);
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(SqliteAccountStore.CurrentSchemaVersion, await ScalarLongAsync(verify, "SELECT version FROM schema_info;"));
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
