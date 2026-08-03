using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;

namespace RelayCove.Client.Tests.Auth;

public sealed class ClientCredentialStoreTests
{
    private static readonly Guid UserId =
        Guid.Parse("4a62bf74-3131-42f0-b8a2-7355a6b349ec");
    private const string RefreshToken = "classified-refresh-token";
    private const string RotatedRefreshToken = "rotated-refresh-token";
    private const string CanonicalServerBaseUri = "https://example.com/proxy/";

    [Fact]
    public async Task SaveAndLoadAsync_WhenCredentialIsValid_UsesDpapiAndRoundTrips()
    {
        using var directory = new TemporaryDirectory();
        var logger = new RecordingLogger<ClientCredentialStore>();
        var store = new ClientCredentialStore(directory.Path, logger);

        var saved = await store.SaveAsync(
            new Uri("HTTPS://EXAMPLE.COM:443/proxy"),
            UserId,
            RefreshToken);
        var ciphertext = await File.ReadAllBytesAsync(store.CredentialPath);
        var loaded = await store.LoadAsync();

        Assert.True(saved);
        Assert.NotEmpty(ciphertext);
        Assert.False(Contains(ciphertext, Encoding.UTF8.GetBytes(CanonicalServerBaseUri)));
        Assert.False(Contains(ciphertext, Encoding.UTF8.GetBytes(UserId.ToString("D"))));
        Assert.False(Contains(ciphertext, Encoding.UTF8.GetBytes(RefreshToken)));
        Assert.Equal(ClientCredentialReadStatus.Loaded, loaded.Status);
        Assert.NotNull(loaded.Credential);
        Assert.Equal(new Uri(CanonicalServerBaseUri), loaded.Credential.ServerBaseUri);
        Assert.Equal(UserId, loaded.Credential.UserId);
        Assert.Equal(RefreshToken, loaded.Credential.RefreshToken);
        Assert.Empty(logger.Entries);

        var text = store + " " + loaded + " " + loaded.Credential;
        Assert.DoesNotContain(directory.Path, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RefreshToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_WhenRefreshTokenRotates_AtomicallyReplacesCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));
        var firstCiphertext = await File.ReadAllBytesAsync(store.CredentialPath);

        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RotatedRefreshToken));

        var secondCiphertext = await File.ReadAllBytesAsync(store.CredentialPath);
        var loaded = await store.LoadAsync();
        Assert.NotEqual(firstCiphertext, secondCiphertext);
        Assert.Equal(ClientCredentialReadStatus.Loaded, loaded.Status);
        Assert.Equal(RotatedRefreshToken, loaded.Credential!.RefreshToken);
        Assert.False(File.Exists(store.CredentialPath + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenTemporaryFileCannotBeCleared_PreservesPriorCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));
        var temporaryPath = store.CredentialPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);

        bool saved;
        await using (var lockedTemporaryFile = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            saved = await store.SaveAsync(
                new Uri(CanonicalServerBaseUri),
                UserId,
                RotatedRefreshToken);
        }

        var loaded = await store.LoadAsync();
        Assert.False(saved);
        Assert.Equal(ClientCredentialReadStatus.Loaded, loaded.Status);
        Assert.Equal(RefreshToken, loaded.Credential!.RefreshToken);
    }

    [Fact]
    public async Task SaveAsync_WhenAtomicReplaceFails_PreservesPriorCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));

        bool saved;
        await using (var lockedCredential = new FileStream(
            store.CredentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            saved = await store.SaveAsync(
                new Uri(CanonicalServerBaseUri),
                UserId,
                RotatedRefreshToken);
        }

        var loaded = await store.LoadAsync();
        Assert.False(saved);
        Assert.Equal(ClientCredentialReadStatus.Loaded, loaded.Status);
        Assert.Equal(RefreshToken, loaded.Credential!.RefreshToken);
        Assert.False(File.Exists(store.CredentialPath + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenCalledConcurrently_SerializesCompleteReplacements()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var tokens = Enumerable.Range(0, 12)
            .Select(index => $"refresh-token-{index:D2}")
            .ToArray();

        var results = await Task.WhenAll(tokens.Select(token => store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            token)));
        var loaded = await store.LoadAsync();

        Assert.All(results, Assert.True);
        Assert.Equal(ClientCredentialReadStatus.Loaded, loaded.Status);
        Assert.Contains(loaded.Credential!.RefreshToken, tokens);
        Assert.False(File.Exists(store.CredentialPath + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_WhenCredentialDoesNotExist_ReturnsNotFound()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);

        var outcome = await store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.NotFound, outcome.Status);
        Assert.Null(outcome.Credential);
    }

    [Fact]
    public async Task LoadAsync_WhenCiphertextIsTampered_ReturnsCorruptAndPreservesFile()
    {
        using var directory = new TemporaryDirectory();
        var logger = new RecordingLogger<ClientCredentialStore>();
        var store = new ClientCredentialStore(directory.Path, logger);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));
        var ciphertext = await File.ReadAllBytesAsync(store.CredentialPath);
        ciphertext[^1] ^= 0xff;
        await File.WriteAllBytesAsync(store.CredentialPath, ciphertext);

        var outcome = await store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.Corrupt, outcome.Status);
        Assert.Null(outcome.Credential);
        Assert.True(File.Exists(store.CredentialPath));
        var logs = string.Join(' ', logger.Entries);
        Assert.DoesNotContain(directory.Path, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RefreshToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_WhenCiphertextIsTruncated_ReturnsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        await File.WriteAllBytesAsync(store.CredentialPath, [1, 2, 3, 4]);

        var outcome = await store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.Corrupt, outcome.Status);
        Assert.Null(outcome.Credential);
    }

    [Fact]
    public async Task LoadAsync_WhenCiphertextExceedsLimit_ReturnsCorruptWithoutDecrypting()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        await File.WriteAllBytesAsync(store.CredentialPath, new byte[(64 * 1024) + 1]);

        var outcome = await store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.Corrupt, outcome.Status);
        Assert.Null(outcome.Credential);
    }

    [Theory]
    [InlineData(2, CanonicalServerBaseUri, "classified-refresh-token")]
    [InlineData(1, "HTTPS://EXAMPLE.COM:443/proxy", "classified-refresh-token")]
    [InlineData(1, CanonicalServerBaseUri, "bad token")]
    public async Task LoadAsync_WhenProtectedPayloadIsInvalid_ReturnsCorrupt(
        int schemaVersion,
        string serverBaseUri,
        string refreshToken)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        WriteProtectedPayload(
            store.CredentialPath,
            schemaVersion,
            serverBaseUri,
            UserId,
            refreshToken);

        var outcome = await store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.Corrupt, outcome.Status);
        Assert.Null(outcome.Credential);
    }

    [Fact]
    public async Task LoadAndClearAsync_WhenCredentialPathIsDirectory_ReportFailure()
    {
        using var directory = new TemporaryDirectory();
        var logger = new RecordingLogger<ClientCredentialStore>();
        var store = new ClientCredentialStore(directory.Path, logger);
        Directory.CreateDirectory(store.CredentialPath);

        var loaded = await store.LoadAsync();
        var cleared = await store.ClearAsync();

        Assert.Equal(ClientCredentialReadStatus.Unavailable, loaded.Status);
        Assert.False(cleared);
        var logs = string.Join(' ', logger.Entries);
        Assert.DoesNotContain(directory.Path, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearAsync_WhenCalledRepeatedly_IsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));

        Assert.True(await store.ClearAsync());
        Assert.True(await store.ClearAsync());
        Assert.Equal(
            ClientCredentialReadStatus.NotFound,
            (await store.LoadAsync()).Status);
    }

    [Fact]
    public async Task Operations_WhenCanceled_PropagateWithoutChangingCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        Assert.True(await store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RefreshToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            RotatedRefreshToken,
            cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ClearAsync(cancellation.Token));

        var loaded = await store.LoadAsync();
        Assert.Equal(RefreshToken, loaded.Credential!.RefreshToken);
    }

    [Fact]
    public async Task SaveAsync_WhenArgumentsAreInvalid_RejectsBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new Uri("ftp://example.com/"),
            UserId,
            RefreshToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            Guid.Empty,
            RefreshToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new Uri(CanonicalServerBaseUri),
            UserId,
            "bad token"));
        Assert.False(File.Exists(store.CredentialPath));
    }

    [Fact]
    public void Constructor_WhenRootIsRelative_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ClientCredentialStore(
            "relative-root",
            new RecordingLogger<ClientCredentialStore>()));
    }

    private static ClientCredentialStore CreateStore(string rootDirectory) =>
        new(rootDirectory, new RecordingLogger<ClientCredentialStore>());

    private static bool Contains(byte[] source, byte[] value) =>
        source.AsSpan().IndexOf(value) >= 0;

    private static void WriteProtectedPayload(
        string path,
        int schemaVersion,
        string serverBaseUri,
        Guid userId,
        string refreshToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion,
                serverBaseUri,
                userId,
                refreshToken,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var entropy = Encoding.UTF8.GetBytes("RelayCove.Client.CredentialStore.v1");
        var ciphertext = ProtectedData.Protect(
            plaintext,
            entropy,
            DataProtectionScope.CurrentUser);
        try
        {
            File.WriteAllBytes(path, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            var testRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RelayCove.Client.Tests"));
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                testRoot,
                Guid.NewGuid().ToString("N")));
            var relativePath = System.IO.Path.GetRelativePath(testRoot, Path);
            if (System.IO.Path.IsPathFullyQualified(relativePath) ||
                relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Test directory escaped its root.");
            }

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
