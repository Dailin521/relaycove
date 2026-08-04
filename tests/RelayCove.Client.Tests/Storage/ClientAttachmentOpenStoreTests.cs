using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Tests.Storage;

public sealed class ClientAttachmentOpenStoreTests : IDisposable
{
    private static readonly Uri ServerBaseUri = new("https://relaycove.test/");
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), "RelayCove-open-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateCopyAsync_WhenVerifiedSourceAndLeafAreValid_WritesRestrictedMOTWAndUsesRandomExtensionOnlyName()
    {
        var bytes = "restricted attachment"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);

        var outcome = await openStore.CreateCopyAsync(
            source,
            "Quarterly Report.PDF",
            bytes.LongLength,
            Hash(bytes));

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, outcome.Status);
        var lease = Assert.IsType<ClientAttachmentOpenLease>(outcome.Lease);
        Assert.Equal(openStore.ScopeDirectory, Path.GetDirectoryName(lease.LocalPath));
        Assert.Matches(new Regex("\\A[0-9a-f]{32}\\.pdf\\z"), Path.GetFileName(lease.LocalPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(lease.LocalPath));
        Assert.Equal(
            "[ZoneTransfer]\r\nZoneId=4\r\n",
            await File.ReadAllTextAsync(lease.LocalPath + ":Zone.Identifier"));
        Assert.DoesNotContain(lease.LocalPath, lease.ToString(), StringComparison.OrdinalIgnoreCase);

        var path = lease.LocalPath;
        await lease.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("document.")]
    [InlineData("document.txt:alternate")]
    [InlineData("folder\\document.txt")]
    [InlineData("document．txt")]
    [InlineData(".hidden.txt")]
    [InlineData("CON.txt")]
    [InlineData("COM1.txt")]
    [InlineData("right-to-left\u202E.txt")]
    [InlineData("document.abcdefghijklmnopq")]
    public async Task CreateCopyAsync_WhenLeafDoesNotHaveSafeTerminalExtension_RejectsWithoutCreatingCopy(string fileName)
    {
        var bytes = "copy input"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);

        var outcome = await openStore.CreateCopyAsync(source, fileName, bytes.LongLength, Hash(bytes));

        Assert.Equal(ClientAttachmentOpenStoreStatus.InvalidFileName, outcome.Status);
        Assert.Null(outcome.Lease);
        Assert.False(Directory.Exists(openStore.ScopeDirectory));
    }

    [Fact]
    public async Task CreateCopyAsync_WhenExecutableLeafIsValid_LeavesHandlerPolicyToRestrictedZone()
    {
        var bytes = "not executed by this store"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);

        var outcome = await openStore.CreateCopyAsync(source, "payload.exe", bytes.LongLength, Hash(bytes));

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, outcome.Status);
        await outcome.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CreateCopyAsync_WhenTerminalExtensionIsUppercaseOrMultiPart_CanonicalizesOnlyTheTerminalAsciiExtension()
    {
        var bytes = "extension policy"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);

        var upper = await openStore.CreateCopyAsync(source, "report.PDF", bytes.Length, Hash(bytes));
        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, upper.Status);
        Assert.EndsWith(".pdf", upper.Lease!.LocalPath, StringComparison.Ordinal);
        await upper.Lease.DisposeAsync();

        using var secondSource = await CreateValidatedSourceAsync(bytes);
        var multiPart = await openStore.CreateCopyAsync(secondSource, "archive.tar.gz", bytes.Length, Hash(bytes));
        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, multiPart.Status);
        Assert.EndsWith(".gz", multiPart.Lease!.LocalPath, StringComparison.Ordinal);
        await multiPart.Lease.DisposeAsync();
    }

    [Fact]
    public async Task CreateCopyAsync_WhenExpectedHashDoesNotMatch_DeletesPrecommitCopy()
    {
        var bytes = "integrity input"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);

        var outcome = await openStore.CreateCopyAsync(
            source,
            "input.txt",
            bytes.LongLength,
            new string('0', 64));

        Assert.Equal(ClientAttachmentOpenStoreStatus.ValidationFailed, outcome.Status);
        Assert.Null(outcome.Lease);
        Assert.Empty(Directory.EnumerateFiles(openStore.ScopeDirectory));
    }

    [Fact]
    public async Task CreateCopyAsync_WhenReservationReachesByteQuota_RejectsThenDisposalReleasesQuota()
    {
        var bytes = "quota"u8.ToArray();
        var openStore = CreateOpenStore(quotaBytes: bytes.Length);
        using var firstSource = await CreateValidatedSourceAsync(bytes);
        using var secondSource = await CreateValidatedSourceAsync(bytes);
        var first = await openStore.CreateCopyAsync(firstSource, "first.txt", bytes.Length, Hash(bytes));
        var blocked = await openStore.CreateCopyAsync(secondSource, "second.txt", bytes.Length, Hash(bytes));

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, first.Status);
        Assert.Equal(ClientAttachmentOpenStoreStatus.QuotaExceeded, blocked.Status);
        await first.Lease!.DisposeAsync();

        var released = await openStore.CreateCopyAsync(secondSource, "second.txt", bytes.Length, Hash(bytes));
        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, released.Status);
        await released.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CreateCopyAsync_WhenAccountFileCountLimitIsReached_ReturnsStoreFull()
    {
        var bytes = "count"u8.ToArray();
        var openStore = CreateOpenStore(maximumFileCount: 1);
        using var firstSource = await CreateValidatedSourceAsync(bytes);
        using var secondSource = await CreateValidatedSourceAsync(bytes);
        var first = await openStore.CreateCopyAsync(firstSource, "first.txt", bytes.Length, Hash(bytes));
        var blocked = await openStore.CreateCopyAsync(secondSource, "second.txt", bytes.Length, Hash(bytes));

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, first.Status);
        Assert.Equal(ClientAttachmentOpenStoreStatus.StoreFull, blocked.Status);
        await first.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CleanupCommittedAsync_WhenLaunchIsActive_DefersDeletionUntilExecuteAttemptCompletes()
    {
        var bytes = "committed copy"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);
        var copy = await openStore.CreateCopyAsync(source, "copy.txt", bytes.Length, Hash(bytes));
        var lease = copy.Lease!;
        var path = lease.LocalPath;
        lease.Commit();

        var requested = await openStore.CleanupCommittedAsync();

        Assert.Equal(ClientAttachmentOpenStoreStatus.CleanupPending, requested.Status);
        Assert.True(File.Exists(path));
        var cleaned = await openStore.CompleteLaunchAsync(lease);

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, cleaned.Status);
        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => _ = lease.LocalPath);
    }

    [Fact]
    public async Task RecoverOrphansAsync_WhenAnotherRuntimeHasActiveLease_DoesNotDeleteIt()
    {
        var bytes = "runtime generation"u8.ToArray();
        var first = CreateOpenStore();
        var nextGeneration = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);
        var copy = await first.CreateCopyAsync(source, "copy.txt", bytes.Length, Hash(bytes));
        var lease = copy.Lease!;
        var path = lease.LocalPath;

        var recovered = await nextGeneration.RecoverOrphansAsync();

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, recovered.Status);
        Assert.Equal(1, recovered.ActiveLeaseCount);
        Assert.True(File.Exists(path));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Lease_WhenCommitRacesPrecommitDispose_HasExactlyOneTerminalOwnershipPath()
    {
        var bytes = "atomic lease state"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);
        var copy = await openStore.CreateCopyAsync(source, "copy.txt", bytes.Length, Hash(bytes));
        var lease = copy.Lease!;
        var path = lease.LocalPath;

        await Task.WhenAll(
            Task.Run(lease.Commit),
            lease.DisposeAsync().AsTask());

        if (lease.IsCommitted)
        {
            Assert.True(File.Exists(path));
            var requested = await openStore.CleanupCommittedAsync();
            Assert.Equal(ClientAttachmentOpenStoreStatus.CleanupPending, requested.Status);
            await openStore.CompleteLaunchAsync(lease);
        }
        else
        {
            Assert.True(lease.IsDisposed);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task RecoverOrphansAsync_WhenManagedOrphanExists_DeletesOnlyManagedFile()
    {
        var openStore = CreateOpenStore();
        Directory.CreateDirectory(openStore.ScopeDirectory);
        var orphan = Path.Combine(openStore.ScopeDirectory, "0123456789abcdef0123456789abcdef.txt");
        await File.WriteAllTextAsync(orphan, "orphan");

        var recovered = await openStore.RecoverOrphansAsync();

        Assert.Equal(ClientAttachmentOpenStoreStatus.Ready, recovered.Status);
        Assert.Equal(1, recovered.DeletedCount);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task RecoverOrphansAsync_WhenScopeIsReparsePoint_FailsClosed()
    {
        var openStore = CreateOpenStore();
        var target = Path.Combine(rootDirectory, "junction-target");
        Directory.CreateDirectory(openStore.RootDirectory);
        Directory.CreateDirectory(target);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", openStore.ScopeDirectory, target },
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);

        var recovered = await openStore.RecoverOrphansAsync();

        Assert.Equal(ClientAttachmentOpenStoreStatus.StorageFailure, recovered.Status);
        Directory.Delete(openStore.ScopeDirectory, recursive: false);
    }

    [Fact]
    public async Task Results_WhenFormatted_DoNotRevealPathOrHash()
    {
        var bytes = "redacted results"u8.ToArray();
        var openStore = CreateOpenStore();
        using var source = await CreateValidatedSourceAsync(bytes);
        var copy = await openStore.CreateCopyAsync(source, "copy.txt", bytes.Length, Hash(bytes));
        var formatted = string.Join("\n", openStore, copy, copy.Lease!);

        Assert.DoesNotContain(rootDirectory, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(copy.Lease!.LocalPath, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Hash(bytes), formatted, StringComparison.Ordinal);
        await copy.Lease.DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private ClientAttachmentOpenStore CreateOpenStore(
        long? quotaBytes = null,
        int? maximumFileCount = null) =>
        new(
            AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory),
            Path.Combine(rootDirectory, "temp"),
            quotaBytes ?? ClientAttachmentOpenStore.DefaultQuotaBytes,
            maximumFileCount ?? ClientAttachmentOpenStore.DefaultMaximumFileCount);

    private async Task<ClientAttachmentCacheStore.ValidatedFile> CreateValidatedSourceAsync(byte[] bytes)
    {
        var cache = new ClientAttachmentCacheStore(
            AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory),
            Path.Combine(rootDirectory, "cache"));
        var key = new ClientAttachmentCacheStoreKey(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.NewGuid(),
            Hash(bytes));
        var staging = (await cache.CreateStagingAsync(key.ConversationId, key.AttachmentId, bytes.Length)).StagingFile!;
        await using (staging)
        {
            await staging.Stream.WriteAsync(bytes);
            await cache.PublishAsync(staging, key.Sha256);
        }

        var resolved = await cache.ValidateAndResolveAsync(
            $"{key.ConversationId:N}.{key.AttachmentId:N}.{key.Sha256}.cache",
            key,
            bytes.Length);
        return Assert.IsType<ClientAttachmentCacheStore.ValidatedFile>(resolved.File);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
