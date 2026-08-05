using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Infrastructure;

public sealed class RelayCoveWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string databaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Tests",
        "Authentication",
        Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly int loginPermitLimit;
    private readonly int refreshPermitLimit;
    private readonly int databaseTimeoutSeconds;
    private readonly IReadOnlyDictionary<string, string?> configurationOverrides;
    private bool initialized;

    public RelayCoveWebApplicationFactory()
        : this(1_000, 1_000, 5, null)
    {
    }

    internal RelayCoveWebApplicationFactory(
        int loginPermitLimit,
        int refreshPermitLimit,
        int databaseTimeoutSeconds = 5,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        this.loginPermitLimit = loginPermitLimit;
        this.refreshPermitLimit = refreshPermitLimit;
        this.databaseTimeoutSeconds = databaseTimeoutSeconds;
        this.configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddTicks(4321));
    }

    public string DatabasePath => Path.Combine(databaseDirectory, "relaycove-auth-tests.db");

    public string UploadsPath => Path.Combine(databaseDirectory, "uploads");

    internal string SigningKey { get; }

    internal MutableTimeProvider Clock { get; }

    internal ConcurrentQueue<string> LogMessages { get; } = new();

    public async Task InitializeDatabaseAsync()
    {
        await initializationGate.WaitAsync();
        try
        {
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(databaseDirectory);
            var dbContextOptions = new DbContextOptionsBuilder<RelayCoveDbContext>()
                .UseSqlite(CreateConnectionString())
                .Options;
            await using (var migrationContext = new RelayCoveDbContext(dbContextOptions))
            {
                await migrationContext.Database.MigrateAsync();
            }

            _ = CreateClient();
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<Guid> CreateUserAsync(
        string userName,
        string password,
        bool isDisabled = false,
        bool isAdmin = false,
        int? passwordIterationCount = null)
    {
        await InitializeDatabaseAsync();
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var passwordService = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var userNameNormalizer = scope.ServiceProvider.GetRequiredService<UserNameNormalizer>();
        var now = scope.ServiceProvider.GetRequiredService<ServerClock>().UtcNow;
        var user = new User(
            Guid.NewGuid(),
            userName,
            userName,
            "pending-password-hash",
            isAdmin,
            isDisabled,
            now,
            userNameNormalizer);
        var passwordHash = passwordIterationCount is null
            ? passwordService.HashPassword(user, password)
            : CreatePasswordService(passwordIterationCount.Value).HashPassword(user, password);
        user.SetPasswordHash(passwordHash, now);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    public async Task SetUserDisabledAsync(Guid userId, bool isDisabled)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var user = await dbContext.Users.SingleAsync(candidate => candidate.Id == userId);
        dbContext.Entry(user).Property(candidate => candidate.IsDisabled).CurrentValue = isDisabled;
        await dbContext.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = CreateConnectionString();
        foreach (var pair in configurationOverrides)
        {
            if (pair.Value is not null)
            {
                builder.UseSetting(pair.Key, pair.Value);
            }
        }
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Authentication:SigningKey"] = SigningKey,
            ["Authentication:LoginPermitLimit"] = loginPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Authentication:RefreshPermitLimit"] = refreshPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Storage:UploadsPath"] = UploadsPath,
        };
        foreach (var pair in configurationOverrides)
        {
            settings[pair.Key] = pair.Value;
        }

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<RelayCoveDbContext>();
            services.RemoveAll<DbContextOptions<RelayCoveDbContext>>();
            services.AddDbContext<RelayCoveDbContext>(options => options.UseSqlite(connectionString));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ILoggerProvider>(_ => new InMemoryLoggerProvider(LogMessages));
        });
    }

    private static PasswordService CreatePasswordService(int iterationCount)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount,
        });
        return new PasswordService(new PasswordHasher<User>(options));
    }

    private string CreateConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        DefaultTimeout = databaseTimeoutSeconds,
        ForeignKeys = true,
        Pooling = false,
    }.ToString();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(databaseDirectory))
        {
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value)
        {
            utcNow = value;
        }
    }

    private sealed class InMemoryLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new InMemoryLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(exception is null
                ? formatter(state, null)
                : $"{formatter(state, exception)} {exception}");
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
