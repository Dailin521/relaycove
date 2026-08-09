using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Hosting;
using RelayCove.Server.Options;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Tests.Hosting;

public sealed class BootstrapAdminHostedServiceTests
{
    private const string UserName = "bootstrap-admin";
    private const string DisplayName = "Bootstrap Administrator";
    private const string Password = "a secure bootstrap phrase";

    [Fact]
    public void Startup_WhenBootstrapIsDisabled_DoesNotAccessOrCreateDatabase()
    {
        using var factory = new RelayCoveWebApplicationFactory();

        using var client = factory.CreateClient();

        Assert.False(File.Exists(factory.DatabasePath));
    }

    [Fact]
    public async Task Startup_WhenEnabledAgainstMigratedEmptyDatabase_CreatesOneLoginCapableAdministrator()
    {
        using var factory = CreateBootstrapFactory();

        await factory.InitializeDatabaseAsync();
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<BootstrapAdminHostedService>()
            .Single();
        await hostedService.StartAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var passwordService = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var user = await dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(UserName, user.UserName);
        Assert.Equal(DisplayName, user.DisplayName);
        Assert.True(user.IsAdmin);
        Assert.False(user.IsDisabled);
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Equal(PasswordVerificationOutcome.Success, passwordService.VerifyPassword(user, user.PasswordHash, Password));

        var defaultChannel = await dbContext.Conversations
            .Include(conversation => conversation.Members)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(ConversationType.PublicChannel, defaultChannel.Type);
        Assert.Equal("general", defaultChannel.Name);
        Assert.Equal(user.Id, defaultChannel.CreatedByUserId);
        var creatorMembership = Assert.Single(defaultChannel.Members);
        Assert.Equal(user.Id, creatorMembership.UserId);
        Assert.Equal(ConversationMemberRole.Administrator, creatorMembership.Role);

        using var client = factory.CreateClient();
        using var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(UserName, Password, "bootstrap-test", "1.0.0"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);

        var logs = string.Join(Environment.NewLine, factory.LogMessages);
        Assert.DoesNotContain(Password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(UserName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayName, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenAnyUserExists_DoesNotCreatePromoteOrChangePassword()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        var existingUserName = $"existing-{Guid.NewGuid():N}";
        var existingUserId = await factory.CreateUserAsync(existingUserName, "an existing secure phrase");
        string originalPasswordHash;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var beforeContext = beforeScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            originalPasswordHash = (await beforeContext.Users.AsNoTracking().SingleAsync()).PasswordHash;
        }

        var service = new BootstrapAdminHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new BootstrapAdminOptions
            {
                Enabled = true,
                UserName = UserName,
                DisplayName = DisplayName,
                Password = Password,
            }),
            factory.Services.GetRequiredService<ILogger<BootstrapAdminHostedService>>());

        await service.StartAsync(CancellationToken.None);

        await using var afterScope = factory.Services.CreateAsyncScope();
        var afterContext = afterScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var users = await afterContext.Users.AsNoTracking().ToArrayAsync();
        var existingUser = Assert.Single(users);
        Assert.Equal(existingUserId, existingUser.Id);
        Assert.False(existingUser.IsAdmin);
        Assert.Equal(originalPasswordHash, existingUser.PasswordHash);
        Assert.Empty(await afterContext.Conversations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Startup_WhenBootstrapConfigurationIsInvalid_FailsWithoutEchoingPassword()
    {
        const string invalidPassword = "bootstrap-admin-bootstrap-admin";
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Enabled"] = "true",
                ["BootstrapAdmin:UserName"] = UserName,
                ["BootstrapAdmin:DisplayName"] = DisplayName,
                ["BootstrapAdmin:Password"] = invalidPassword,
            });

        var exception = await Assert.ThrowsAnyAsync<Exception>(factory.InitializeDatabaseAsync);

        Assert.Contains("BootstrapAdmin:Password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(invalidPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_WhenBootstrapIsEnabledBeforeMigration_FailsWithoutCreatingSchemaOrEchoingPassword()
    {
        using var factory = CreateBootstrapFactory();
        Directory.CreateDirectory(Path.GetDirectoryName(factory.DatabasePath)!);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = factory.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Users';";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static RelayCoveWebApplicationFactory CreateBootstrapFactory() => new(
        1_000,
        1_000,
        configurationOverrides: new Dictionary<string, string?>
        {
            ["BootstrapAdmin:Enabled"] = "true",
            ["BootstrapAdmin:UserName"] = UserName,
            ["BootstrapAdmin:DisplayName"] = DisplayName,
            ["BootstrapAdmin:Password"] = Password,
        });
}
