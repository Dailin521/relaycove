using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientAttachmentDownloadCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.DownloadCoordinator.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoverAsync_WhenDbPathIsMissingAndStoreHasOrphans_ResetsDbAndDeletesFiles()
    {
        await using var prepared = await CreatePreparedAsync();
        var hash = new string('a', 64);
        var path = ManagedPath(prepared.Conversation.Id, prepared.Attachment.Id, hash);
        Assert.Equal(LocalAttachmentDownloadClaimResult.Claimed, (await prepared.Cache
            .ClaimAttachmentDownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id)).Result);
        Assert.Equal(LocalCacheOperationStatus.Ready, await prepared.Cache.CompleteAttachmentDownloadAsync(
            prepared.Conversation.Id, prepared.Attachment.Id, path));

        var orphanConversation = Guid.NewGuid();
        var orphanAttachment = Guid.NewGuid();
        var orphanFinal = ManagedPath(orphanConversation, orphanAttachment, new string('b', 64));
        var orphanStaging = $"{orphanConversation:N}.{orphanAttachment:N}.{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(prepared.Store.ScopeDirectory);
        await File.WriteAllBytesAsync(Path.Combine(prepared.Store.ScopeDirectory, orphanFinal), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(prepared.Store.ScopeDirectory, orphanStaging), [3]);

        await using var coordinator = CreateCoordinator(prepared, new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Recovery must not issue HTTP."))));

        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        Assert.Null(TextScalarOrNull(prepared.Identity, "SELECT LocalPath FROM LocalAttachments;"));
        Assert.False(File.Exists(Path.Combine(prepared.Store.ScopeDirectory, orphanFinal)));
        Assert.False(File.Exists(Path.Combine(prepared.Store.ScopeDirectory, orphanStaging)));
    }

    [Fact]
    public async Task DownloadAsync_WhenVerified_CompletesAtomicallyAndReusesDownloadedFile()
    {
        var payload = "coordinator payload"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        var requests = 0;
        var progress = new List<ClientAttachmentDownloadProgress>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Ok(payload, prepared.Attachment));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());

        var first = await coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            progress: new Progress<ClientAttachmentDownloadProgress>(progress.Add));
        var second = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, first.Status);
        Assert.NotNull(first.LocalPath);
        Assert.Equal(ClientAttachmentDownloadStatus.AlreadyDownloaded, second.Status);
        Assert.Equal(first.LocalPath, second.LocalPath);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(prepared.Store.ScopeDirectory, first.LocalPath!)));
        Assert.Equal(2, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        var finalProgress = Assert.Single(progress);
        Assert.Equal(payload.LongLength, finalProgress.BytesWritten);
        Assert.Equal(100, finalProgress.Percent);
        Assert.DoesNotContain((await prepared.Store.EnumerateAsync()).Entries, entry =>
            entry.Kind == ClientAttachmentCacheStoreEntryKind.Staging);
    }

    [Fact]
    public async Task DownloadAsync_WhenCorruptFinalAppearsAfterRecovery_ReplacesItWithoutRestart()
    {
        var payload = "fresh attachment"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Ok(payload, prepared.Attachment));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());
        var relativePath = ManagedPath(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            Sha256(payload));
        await File.WriteAllBytesAsync(
            Path.Combine(prepared.Store.ScopeDirectory, relativePath),
            new byte[payload.Length]);

        var outcome = await coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, outcome.Status);
        Assert.Equal(relativePath, outcome.LocalPath);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(
                Path.Combine(prepared.Store.ScopeDirectory, relativePath)));
    }

    [Fact]
    public async Task DownloadAsync_WhenRevokedAfterExistingValidation_DoesNotReturnCachedPath()
    {
        var payload = "cached attachment"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        var relativePath = await PublishAsync(
            prepared.Store,
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            payload);
        Assert.Equal(
            LocalAttachmentDownloadClaimResult.Claimed,
            (await prepared.Cache.ClaimAttachmentDownloadAsync(
                prepared.Conversation.Id,
                prepared.Attachment.Id)).Result);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.CompleteAttachmentDownloadAsync(
                prepared.Conversation.Id,
                prepared.Attachment.Id,
                relativePath));
        var blockingStore = new BlockingCacheStore(prepared.Store);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("A cached attachment must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            cacheStore: blockingStore);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());
        blockingStore.BlockNextValidation();

        var download = coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id);
        await blockingStore.ValidationCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await using var revokingCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await revokingCache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        blockingStore.ReleaseValidation();
        var outcome = await download;

        Assert.Equal(ClientAttachmentDownloadStatus.AccessRevoked, outcome.Status);
        Assert.Null(outcome.LocalPath);
    }

    [Fact]
    public async Task DownloadAsync_WhenSameAttachmentIsAlreadyInFlight_UsesOneGet()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var prepared = await CreatePreparedAsync(payload);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Ok(payload, prepared.Attachment);
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();

        var firstTask = coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);
        release.SetResult();
        var first = await firstTask;

        Assert.Equal(ClientAttachmentDownloadStatus.InProgress, second.Status);
        Assert.Equal(ClientAttachmentDownloadStatus.Completed, first.Status);
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task DownloadAsync_WhenCallerCancels_DeletesStagingAndReturnsClaimToReady()
    {
        await using var prepared = await CreatePreparedAsync([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled handler unexpectedly resumed.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();

        var task = coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();
        var outcome = await task;

        Assert.Equal(ClientAttachmentDownloadStatus.Canceled, outcome.Status);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task DownloadAsync_WhenStableRevocation403_RevokesDurablyAndPurgesConversation()
    {
        await using var prepared = await CreatePreparedAsync([1, 2, 3]);
        var notificationPurges = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(
            Error(HttpStatusCode.Forbidden, ApiErrorCodes.ConversationAccessRevoked))));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            (_, _) =>
            {
                Interlocked.Increment(ref notificationPurges);
                return Task.CompletedTask;
            });
        await coordinator.RecoverAsync();
        var prior = await PublishAsync(prepared.Store, prepared.Conversation.Id, Guid.NewGuid(), [9]);

        var outcome = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.AccessRevoked, outcome.Status);
        Assert.Equal(LocalCacheOperationStatus.RevokedConversation,
            prepared.Cache.GetConversationAccessStatus(prepared.Conversation.Id));
        Assert.Equal(1, Volatile.Read(ref notificationPurges));
        Assert.False(File.Exists(Path.Combine(prepared.Store.ScopeDirectory, prior)));
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task DownloadAsync_WhenOtherCacheRevokesWithLockedStaging_DeletesFinalAndRetriesAfterFlightEnds()
    {
        await using var prepared = await CreatePreparedAsync([1, 2, 3]);
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled handler unexpectedly resumed.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();
        var prior = await PublishAsync(
            prepared.Store,
            prepared.Conversation.Id,
            Guid.NewGuid(),
            [9]);

        var download = coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var revokingCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await revokingCache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        var outcome = await download;

        Assert.Equal(ClientAttachmentDownloadStatus.AccessRevoked, outcome.Status);
        Assert.False(File.Exists(Path.Combine(prepared.Store.ScopeDirectory, prior)));
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task DownloadAsync_WhenRevokedDuringPublishFailure_ReportsAccessRevoked()
    {
        var payload = "publish race"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        var blockingStore = new BlockingCacheStore(prepared.Store);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(payload, prepared.Attachment))));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            cacheStore: blockingStore);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());
        blockingStore.BlockNextPublishAsStorageFailure();

        var download = coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id);
        await blockingStore.PublishStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await using var revokingCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await revokingCache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        blockingStore.ReleasePublish();
        var outcome = await download;

        Assert.Equal(ClientAttachmentDownloadStatus.AccessRevoked, outcome.Status);
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task DownloadAsync_WhenOrdinary403_DoesNotPurgeOrRevoke()
    {
        await using var prepared = await CreatePreparedAsync([1, 2, 3]);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(
            Error(HttpStatusCode.Forbidden, "OtherForbidden"))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();
        var prior = await PublishAsync(prepared.Store, prepared.Conversation.Id, Guid.NewGuid(), [9]);

        var outcome = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.AccessDenied, outcome.Status);
        Assert.Equal(LocalCacheOperationStatus.Ready,
            prepared.Cache.GetConversationAccessStatus(prepared.Conversation.Id));
        Assert.True(File.Exists(Path.Combine(prepared.Store.ScopeDirectory, prior)));
        Assert.Equal(3, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_WhenResponseIntegrityFails_DoesNotPublish(bool wrongHash)
    {
        var metadataPayload = new byte[] { 1, 2, 3 };
        var responsePayload = wrongHash ? metadataPayload : new byte[] { 1, 2, 3, 4 };
        await using var prepared = await CreatePreparedAsync(metadataPayload);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            var response = Ok(responsePayload, prepared.Attachment);
            if (wrongHash)
            {
                response.Headers.ETag = new EntityTagHeaderValue($"\"{new string('0', 64)}\"");
            }

            return Task.FromResult(response);
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();

        var outcome = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.ProtocolError, outcome.Status);
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
        Assert.Equal(3, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
    }

    [Fact]
    public async Task DownloadAsync_WhenQuotaCannotReserve_DoesNotIssueGet()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var prepared = await CreatePreparedAsync(payload, quotaBytes: payload.Length - 1);
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Quota failure must precede HTTP.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();

        var outcome = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.QuotaExceeded, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal(3, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
    }

    [Fact]
    public async Task DownloadAsync_WhenSqliteCommitFailsAfterPublish_DeletesFinalAndRecoveryResetsClaim()
    {
        var payload = "atomic boundary"u8.ToArray();
        var faultInjector = new AttachmentDownloadCommitThrowingFaultInjector();
        await using var prepared = await CreatePreparedAsync(
            payload,
            faultInjector: faultInjector);
        faultInjector.ExpectedPath = Path.Combine(
            prepared.Store.ScopeDirectory,
            ManagedPath(
                prepared.Conversation.Id,
                prepared.Attachment.Id,
                Sha256(payload)));
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(payload, prepared.Attachment))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        Assert.Equal(ClientAttachmentCacheRecoveryStatus.Ready, await coordinator.RecoverAsync());

        var outcome = await coordinator.DownloadAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id);

        Assert.Equal(ClientAttachmentDownloadStatus.LocalCacheFailure, outcome.Status);
        Assert.True(faultInjector.ObservedPublishedFile);
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));

        await coordinator.DisposeAsync();
        await prepared.Cache.DisposeAsync();
        AccountScopedLocalCache.ResetProcessStateForTest(prepared.Identity);
        await using var reopenedCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        using var recoveryHttpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Recovery must not issue HTTP.")));
        await using var recoveryCoordinator = new ClientAttachmentDownloadCoordinator(
            reopenedCache,
            prepared.Store,
            new ClientAttachmentDownloadHttpTransport(
                prepared.Identity,
                recoveryHttpClient,
                new FakeAuthenticationSession(),
                NullLogger.Instance),
            NullLogger<ClientAttachmentDownloadCoordinator>.Instance);

        Assert.Equal(
            ClientAttachmentCacheRecoveryStatus.Ready,
            await recoveryCoordinator.RecoverAsync());
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        Assert.Empty((await prepared.Store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task DownloadModels_ToString_RedactsLocalPathsAndIntegrityValues()
    {
        await using var prepared = await CreatePreparedAsync([1, 2, 3]);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(
            Ok([1, 2, 3], prepared.Attachment))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        await coordinator.RecoverAsync();
        var outcome = await coordinator.DownloadAsync(prepared.Conversation.Id, prepared.Attachment.Id);
        var rendered = string.Join(' ', coordinator, outcome, ClientAttachmentDownloadHttpResult.Success(new string('a', 64), 3));

        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.LocalPath!, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenDownloadedFileIsVerified_RevealsValidatedCapability()
    {
        var payload = "verified reveal"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            attachmentShell: shell);

        var outcome = await coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);

        Assert.Equal(ClientAttachmentRevealStatus.Revealed, outcome.Status);
        Assert.Equal(1, shell.RevealCount);
        Assert.NotNull(shell.LastFile);
        Assert.DoesNotContain(
            prepared.Store.ScopeDirectory,
            shell.LastFile!.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenNotDownloaded_DoesNotCallShell()
    {
        await using var prepared = await CreatePreparedAsync();
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            attachmentShell: shell);

        var outcome = await coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);

        Assert.Equal(ClientAttachmentRevealStatus.NotDownloaded, outcome.Status);
        Assert.Equal(0, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenFileIsCorrupt_DoesNotCallShell()
    {
        var payload = "trusted bytes"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        var relativePath = await MarkDownloadedAsync(prepared, payload);
        await File.WriteAllBytesAsync(
            Path.Combine(prepared.Store.ScopeDirectory, relativePath),
            new byte[payload.Length]);
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            attachmentShell: shell);

        var outcome = await coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);

        Assert.Equal(ClientAttachmentRevealStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenDbPathChangesAfterValidation_DoesNotCallShell()
    {
        var payload = "path race"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var blockingStore = new BlockingCacheStore(prepared.Store);
        blockingStore.BlockNextValidation();
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            cacheStore: blockingStore,
            attachmentShell: shell);

        var reveal = coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);
        await blockingStore.ValidationCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        using (var connection = OpenConnection(prepared.Identity))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE LocalAttachments SET LocalPath = $path;";
            command.Parameters.AddWithValue(
                "$path",
                ManagedPath(
                    prepared.Conversation.Id,
                    prepared.Attachment.Id,
                    new string('f', 64)));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        blockingStore.ReleaseValidation();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.Stale, outcome.Status);
        Assert.Equal(0, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenRevokedAfterValidation_DoesNotCallShell()
    {
        var payload = "revocation race"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var blockingStore = new BlockingCacheStore(prepared.Store);
        blockingStore.BlockNextValidation();
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            cacheStore: blockingStore,
            attachmentShell: shell);

        var reveal = coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);
        await blockingStore.ValidationCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await using var revokingCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await revokingCache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        blockingStore.ReleaseValidation();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.AccessRevoked, outcome.Status);
        Assert.Equal(0, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenMetadataChangesAfterValidation_DoesNotCallShell()
    {
        var payload = "metadata race"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var blockingStore = new BlockingCacheStore(prepared.Store);
        blockingStore.BlockNextValidation();
        var shell = new FakeWindowsAttachmentShell(WindowsAttachmentShellStatus.Revealed);
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            cacheStore: blockingStore,
            attachmentShell: shell);

        var reveal = coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);
        await blockingStore.ValidationCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        using (var connection = OpenConnection(prepared.Identity))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE LocalAttachments SET Size = Size + 1;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        blockingStore.ReleaseValidation();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.Stale, outcome.Status);
        Assert.Equal(0, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenShellBlocks_ReleasesCacheGateBeforeNativeCall()
    {
        var payload = "blocked shell"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var shell = new BlockingWindowsAttachmentShell();
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            attachmentShell: shell);

        var revealTask = coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);
        await shell.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // The shell can be arbitrarily slow. A separate cache mutation must not
        // wait on the reveal's SQLite transaction or operation gate.
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.MarkConversationRenderedThroughAsync(
                prepared.Conversation.Id,
                messageId: 1).WaitAsync(TimeSpan.FromSeconds(5)));

        shell.Release();
        var outcome = await revealTask;

        Assert.Equal(ClientAttachmentRevealStatus.Revealed, outcome.Status);
        Assert.Equal(1, shell.RevealCount);
    }

    [Fact]
    public async Task RevealInFolderAsync_WhenShellBlocks_RevocationAndDisposeDoNotWaitForShell()
    {
        var payload = "blocked shell revocation"u8.ToArray();
        await using var prepared = await CreatePreparedAsync(payload);
        await MarkDownloadedAsync(prepared, payload);
        var shell = new BlockingWindowsAttachmentShell();
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            (_, _) => throw new InvalidOperationException("Reveal must not issue HTTP.")));
        var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            attachmentShell: shell);

        var revealTask = coordinator.RevealInFolderAsync(
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            CommitReveal);
        await shell.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await prepared.Cache.RevokeConversationAccessAsync(prepared.Conversation.Id)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        shell.Release();
        var outcome = await revealTask;

        Assert.Equal(ClientAttachmentRevealStatus.Revealed, outcome.Status);
        Assert.Equal(1, shell.RevealCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static ClientAttachmentRevealStatus CommitReveal() =>
        ClientAttachmentRevealStatus.Revealed;

    private async Task<Prepared> CreatePreparedAsync(
        byte[]? payload = null,
        long? quotaBytes = null,
        ILocalCacheFaultInjector? faultInjector = null)
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            faultInjector);
        var conversation = new ConversationDto(
            Guid.NewGuid(), ConversationType.PrivateChannel, "Private", null,
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-04T01:00:00Z"), 1, 1, 1);
        Assert.Equal(LocalCacheOperationStatus.Ready, await cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse([conversation], Complete: true)));
        var bytes = payload ?? new byte[] { 1, 2, 3 };
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId, "private.png", "image/png", bytes.LongLength,
            $"/api/attachments/{attachmentId:D}/download", ThumbnailUrl: null);
        var message = new MessageDto(
            1, Guid.NewGuid(), conversation.Id, Guid.NewGuid(), "Sender", MessageType.Image,
            Content: null, ReplyToMessageId: null, Attachments: [attachment],
            MentionUserIds: Array.Empty<Guid>(), CreatedAt: DateTimeOffset.UtcNow);
        Assert.Equal(IncomingMessageMergeResult.Inserted, (await cache.MergeIncomingMessageAsync(message)).Result);
        var store = new ClientAttachmentCacheStore(
            identity,
            Path.Combine(rootDirectory, "cache"),
            quotaBytes ?? ClientAttachmentCacheStore.DefaultQuotaBytes);
        return new Prepared(identity, cache, store, conversation, attachment);
    }

    private static ClientAttachmentDownloadCoordinator CreateCoordinator(
        Prepared prepared,
        HttpClient httpClient,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        IClientAttachmentCacheStore? cacheStore = null,
        IWindowsAttachmentShell? attachmentShell = null) =>
        new(
            prepared.Cache,
            cacheStore ?? prepared.Store,
            new ClientAttachmentDownloadHttpTransport(
                prepared.Identity,
                httpClient,
                new FakeAuthenticationSession(),
                NullLogger.Instance),
            NullLogger<ClientAttachmentDownloadCoordinator>.Instance,
            conversationRevokedAsync,
            attachmentShell);

    private static async Task<string> MarkDownloadedAsync(
        Prepared prepared,
        byte[] payload)
    {
        var relativePath = await PublishAsync(
            prepared.Store,
            prepared.Conversation.Id,
            prepared.Attachment.Id,
            payload);
        Assert.Equal(
            LocalAttachmentDownloadClaimResult.Claimed,
            (await prepared.Cache.ClaimAttachmentDownloadAsync(
                prepared.Conversation.Id,
                prepared.Attachment.Id)).Result);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.CompleteAttachmentDownloadAsync(
                prepared.Conversation.Id,
                prepared.Attachment.Id,
                relativePath));
        return relativePath;
    }

    private static async Task<string> PublishAsync(
        ClientAttachmentCacheStore store,
        Guid conversationId,
        Guid attachmentId,
        byte[] payload)
    {
        var staging = await store.CreateStagingAsync(conversationId, attachmentId, payload.LongLength);
        await using var file = Assert.IsType<ClientAttachmentCacheStoreStagingFile>(staging.StagingFile);
        await file.Stream.WriteAsync(payload);
        var published = await store.PublishAsync(file, Sha256(payload));
        return Assert.IsType<string>(published.RelativePath);
    }

    private static HttpResponseMessage Ok(byte[] payload, AttachmentDto attachment)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.ETag = new EntityTagHeaderValue($"\"{Sha256(payload)}\"");
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string code) => new(statusCode)
    {
        Content = JsonContent.Create(new ApiErrorResponse(code, "A stable error occurred.")),
    };

    private static string ManagedPath(Guid conversationId, Guid attachmentId, string hash) =>
        $"{conversationId:N}.{attachmentId:N}.{hash}.cache";

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? TextScalarOrNull(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : Convert.ToString(value);
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

    private sealed record Prepared(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ClientAttachmentCacheStore Store,
        ConversationDto Conversation,
        AttachmentDto Attachment) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cache.DisposeAsync();
    }

    private sealed class FakeAuthenticationSession : IClientAuthenticationSession
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("access-token");

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeWindowsAttachmentShell(WindowsAttachmentShellStatus status) :
        IWindowsAttachmentShell
    {
        public int RevealCount { get; private set; }

        public ClientAttachmentCacheStore.ValidatedFile? LastFile { get; private set; }

        public WindowsAttachmentShellStatus Reveal(
            ClientAttachmentCacheStore.ValidatedFile file)
        {
            RevealCount++;
            LastFile = file;
            return status;
        }
    }

    private sealed class BlockingWindowsAttachmentShell : IWindowsAttachmentShell
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int revealCount;

        public Task Started => started.Task;

        public int RevealCount => Volatile.Read(ref revealCount);

        public WindowsAttachmentShellStatus Reveal(
            ClientAttachmentCacheStore.ValidatedFile file)
        {
            Interlocked.Increment(ref revealCount);
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return WindowsAttachmentShellStatus.Revealed;
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class AttachmentDownloadCommitThrowingFaultInjector : ILocalCacheFaultInjector
    {
        public string? ExpectedPath { get; set; }

        public bool ObservedPublishedFile { get; private set; }

        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeAttachmentDownloadCommit()
        {
            ObservedPublishedFile = ExpectedPath is not null && File.Exists(ExpectedPath);
            throw new InvalidOperationException("Injected attachment commit failure.");
        }
    }

    private sealed class BlockingCacheStore(IClientAttachmentCacheStore inner) :
        IClientAttachmentCacheStore
    {
        private readonly TaskCompletionSource validationCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource validationRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource publishStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource publishRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockValidation;
        private int blockPublish;

        public Task ValidationCompleted => validationCompleted.Task;

        public Task PublishStarted => publishStarted.Task;

        public void BlockNextValidation() => Interlocked.Exchange(ref blockValidation, 1);

        public void ReleaseValidation() => validationRelease.TrySetResult();

        public void BlockNextPublishAsStorageFailure() =>
            Interlocked.Exchange(ref blockPublish, 1);

        public void ReleasePublish() => publishRelease.TrySetResult();

        public Task<ClientAttachmentCacheStoreStagingOutcome> CreateStagingAsync(
            Guid conversationId,
            Guid attachmentId,
            long expectedSize,
            CancellationToken cancellationToken = default) =>
            inner.CreateStagingAsync(
                conversationId,
                attachmentId,
                expectedSize,
                cancellationToken);

        public async Task<ClientAttachmentCacheStorePublishOutcome> PublishAsync(
            ClientAttachmentCacheStoreStagingFile stagingFile,
            string verifiedLowercaseSha256,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref blockPublish, 0) != 0)
            {
                publishStarted.TrySetResult();
                await publishRelease.Task.ConfigureAwait(false);
                return new ClientAttachmentCacheStorePublishOutcome(
                    ClientAttachmentCacheStoreStatus.StorageFailure,
                    RelativePath: null);
            }

            return await inner.PublishAsync(
                stagingFile,
                verifiedLowercaseSha256,
                cancellationToken);
        }

        public async Task<ClientAttachmentCacheStoreValidationOutcome> ValidateAsync(
            string relativePath,
            ClientAttachmentCacheStoreKey expectedKey,
            long expectedSize,
            CancellationToken cancellationToken = default)
        {
            var outcome = await inner.ValidateAsync(
                relativePath,
                expectedKey,
                expectedSize,
                cancellationToken);
            if (Interlocked.Exchange(ref blockValidation, 0) != 0)
            {
                validationCompleted.TrySetResult();
                await validationRelease.Task.ConfigureAwait(false);
            }

            return outcome;
        }

        public async Task<ClientAttachmentCacheStoreResolutionOutcome> ValidateAndResolveAsync(
            string relativePath,
            ClientAttachmentCacheStoreKey expectedKey,
            long expectedSize,
            CancellationToken cancellationToken = default)
        {
            var outcome = await inner.ValidateAndResolveAsync(
                relativePath,
                expectedKey,
                expectedSize,
                cancellationToken);
            if (Interlocked.Exchange(ref blockValidation, 0) != 0)
            {
                validationCompleted.TrySetResult();
                await validationRelease.Task.ConfigureAwait(false);
            }

            return outcome;
        }

        public Task<ClientAttachmentCacheStoreEnumerationOutcome> EnumerateAsync(
            CancellationToken cancellationToken = default) =>
            inner.EnumerateAsync(cancellationToken);

        public Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(relativePath, cancellationToken);

        public Task<ClientAttachmentCacheStoreDeleteOutcome> DeleteConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            inner.DeleteConversationAsync(conversationId, cancellationToken);

        public Task<ClientAttachmentCacheStoreQuotaOutcome> GetQuotaAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetQuotaAsync(cancellationToken);
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }
}
