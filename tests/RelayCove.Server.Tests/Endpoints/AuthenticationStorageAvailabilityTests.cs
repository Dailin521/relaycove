using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Errors;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class AuthenticationStorageAvailabilityTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-horse-battery-staple";
    private readonly RelayCoveWebApplicationFactory factory = new(1_000, 1_000, 1);

    public Task InitializeAsync() => factory.InitializeDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_WhenDatabaseIsLocked_ReturnsStableServiceUnavailableEnvelope()
    {
        var userName = $"locked-{Guid.NewGuid():N}";
        await factory.CreateUserAsync(userName, Password);
        await using var lockingConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = factory.DatabasePath,
            DefaultTimeout = 1,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await lockingConnection.OpenAsync();
        await using var lockCommand = lockingConnection.CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE;";
        await lockCommand.ExecuteNonQueryAsync();
        lockCommand.CommandText = "UPDATE Users SET UpdatedAt = UpdatedAt;";
        await lockCommand.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(userName, Password, "device", "1.0.0"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Equal(ApiErrorCodes.ServiceUnavailable, error!.Code);
            Assert.Equal("The service is temporarily unavailable.", error.Message);
        }
        finally
        {
            lockCommand.CommandText = "ROLLBACK;";
            await lockCommand.ExecuteNonQueryAsync();
        }
    }

    public void Dispose()
    {
        factory.Dispose();
    }
}
