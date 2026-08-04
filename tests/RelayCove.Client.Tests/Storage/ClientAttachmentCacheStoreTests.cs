using System.Diagnostics;
using System.Security.Cryptography;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Tests.Storage;

public sealed class ClientAttachmentCacheStoreTests : IDisposable
{
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCoveAttachmentCacheStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishAsync_WhenContentMatches_PublishesManagedFlatPathAndValidates()
    {
        var store = CreateStore();
        var bytes = "trusted attachment"u8.ToArray();
        var key = CreateKey(bytes);

        var published = await WriteAndPublishAsync(store, key, bytes);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, published.Status);
        var expectedRelativePath =
            $"{key.ConversationId:N}.{key.AttachmentId:N}.{key.Sha256}.cache";
        Assert.Equal(expectedRelativePath, published.RelativePath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(rootDirectory, "cache", store.Identity.Id)),
            store.ScopeDirectory);
        Assert.Equal(
            bytes,
            await File.ReadAllBytesAsync(Path.Combine(store.ScopeDirectory, published.RelativePath!)));

        var validated = await store.ValidateAsync(published.RelativePath!, key, bytes.LongLength);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, validated.Status);
        Assert.True(validated.IsValid);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_WhenContentMatches_ReturnsRedactedCapability()
    {
        var store = CreateStore();
        var bytes = "trusted capability"u8.ToArray();
        var key = CreateKey(bytes);
        var published = await WriteAndPublishAsync(store, key, bytes);

        var resolved = await store.ValidateAndResolveAsync(
            published.RelativePath!,
            key,
            bytes.LongLength);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, resolved.Status);
        using var file = Assert.IsType<ClientAttachmentCacheStore.ValidatedFile>(resolved.File);
        Assert.Equal(
            Path.Combine(store.ScopeDirectory, published.RelativePath!),
            file.FullPath);
        Assert.DoesNotContain(file.FullPath, file.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", resolved.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_WhileCapabilityIsAlive_BlocksReplacement()
    {
        var store = CreateStore();
        var bytes = "pinned capability"u8.ToArray();
        var replacement = "other capability!"u8.ToArray();
        Assert.Equal(bytes.Length, replacement.Length);
        var key = CreateKey(bytes);
        var published = await WriteAndPublishAsync(store, key, bytes);
        var fullPath = Path.Combine(store.ScopeDirectory, published.RelativePath!);
        var resolved = await store.ValidateAndResolveAsync(
            published.RelativePath!,
            key,
            bytes.LongLength);
        var file = Assert.IsType<ClientAttachmentCacheStore.ValidatedFile>(resolved.File);

        await Assert.ThrowsAsync<IOException>(() =>
            File.WriteAllBytesAsync(fullPath, replacement));
        Assert.Throws<IOException>(() => File.Delete(fullPath));

        file.Dispose();
        await File.WriteAllBytesAsync(fullPath, replacement);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(fullPath));
        Assert.Throws<ObjectDisposedException>(() => _ = file.FullPath);
    }

    [Fact]
    public async Task ValidatedFile_ReadContentAsync_ProvidesPathlessReadOnlyPinnedContent()
    {
        var store = CreateStore();
        var bytes = "pathless validated content"u8.ToArray();
        var key = CreateKey(bytes);
        var published = await WriteAndPublishAsync(store, key, bytes);
        var fullPath = Path.Combine(store.ScopeDirectory, published.RelativePath!);
        var resolved = await store.ValidateAndResolveAsync(
            published.RelativePath!,
            key,
            bytes.LongLength);
        using var file = Assert.IsType<ClientAttachmentCacheStore.ValidatedFile>(resolved.File);

        var read = await file.ReadContentAsync(
            async (content, cancellationToken) =>
            {
                Assert.False(content is FileStream);
                Assert.True(content.CanRead);
                Assert.True(content.CanSeek);
                Assert.False(content.CanWrite);
                Assert.DoesNotContain(
                    fullPath,
                    content.ToString() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                await Assert.ThrowsAsync<NotSupportedException>(() =>
                    content.WriteAsync(new byte[] { 1 }, cancellationToken).AsTask());
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, cancellationToken);
                content.Dispose();
                return copy.ToArray();
            });

        Assert.Equal(bytes, read);
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(fullPath, bytes));
    }

    [Fact]
    public async Task ValidatedFile_ReadContentAsync_AfterCapabilityDisposal_Throws()
    {
        var store = CreateStore();
        var bytes = "disposed validated content"u8.ToArray();
        var key = CreateKey(bytes);
        var published = await WriteAndPublishAsync(store, key, bytes);
        var resolved = await store.ValidateAndResolveAsync(
            published.RelativePath!,
            key,
            bytes.LongLength);
        var file = Assert.IsType<ClientAttachmentCacheStore.ValidatedFile>(resolved.File);
        file.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            file.ReadContentAsync(
                static (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public void ValidatedFile_WhenTokenIsNotStoreOwned_CannotBeForged()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ClientAttachmentCacheStore.ValidatedFile(
                Path.Combine(rootDirectory, "untrusted.cache"),
                stream: null!,
                validationToken: new object()));
    }

    [Fact]
    public async Task PublishAsync_WhenHashOrSizeDoesNotMatch_DeletesStagingWithoutPublishing()
    {
        var store = CreateStore();
        var expected = "expected"u8.ToArray();
        var actual = "differen"u8.ToArray();
        var key = CreateKey(expected);
        var staging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            expected.LongLength)).StagingFile!;
        await using (staging)
        {
            await staging.Stream.WriteAsync(actual);
            var published = await store.PublishAsync(staging, key.Sha256);

            Assert.Equal(ClientAttachmentCacheStoreStatus.ValidationFailed, published.Status);
            Assert.Null(published.RelativePath);
        }

        var entries = await store.EnumerateAsync();

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, entries.Status);
        Assert.Empty(entries.Entries);
    }

    [Fact]
    public async Task PublishAsync_WhenSizeDoesNotMatch_DeletesStagingWithoutPublishing()
    {
        var store = CreateStore();
        var expected = "tiny"u8.ToArray();
        var key = CreateKey(expected);
        var staging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            expected.LongLength)).StagingFile!;
        await using (staging)
        {
            await staging.Stream.WriteAsync("longer"u8.ToArray());
            var published = await store.PublishAsync(staging, key.Sha256);

            Assert.Equal(ClientAttachmentCacheStoreStatus.ValidationFailed, published.Status);
        }

        Assert.Empty((await store.EnumerateAsync()).Entries);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceled_DeletesStagingAndReleasesReservation()
    {
        var store = CreateStore(quotaBytes: 3);
        var bytes = new byte[] { 1, 2, 3 };
        var key = CreateKey(bytes);
        var staging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            bytes.LongLength)).StagingFile!;
        await staging.Stream.WriteAsync(bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.PublishAsync(staging, key.Sha256, cancellation.Token));
        await staging.DisposeAsync();

        Assert.Empty((await store.EnumerateAsync()).Entries);
        var replacement = await store.CreateStagingAsync(
            key.ConversationId,
            Guid.NewGuid(),
            bytes.LongLength);
        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, replacement.Status);
        await replacement.StagingFile!.DisposeAsync();
    }

    [Theory]
    [InlineData("..\\outside.cache")]
    [InlineData("safe.cache:alternate")]
    [InlineData("folder/safe.cache")]
    [InlineData("0123456789abcdef0123456789abcdef.0123456789abcdef0123456789abcdef.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.cache ")]
    public async Task ManagedPathOperations_WhenRelativePathIsUnsafe_RejectWithoutTouchingFiles(string relativePath)
    {
        var store = CreateStore();
        var key = CreateKey("content"u8.ToArray());

        var validation = await store.ValidateAsync(relativePath, key, expectedSize: 7);
        var deletion = await store.DeleteAsync(relativePath);

        Assert.Equal(ClientAttachmentCacheStoreStatus.InvalidRelativePath, validation.Status);
        Assert.False(validation.IsValid);
        Assert.Equal(ClientAttachmentCacheStoreStatus.InvalidRelativePath, deletion.Status);
        Assert.Equal(0, deletion.DeletedCount);
    }

    [Fact]
    public async Task PublishAsync_WhenAccountsDiffer_IsolatesSameAttachmentId()
    {
        var first = CreateStore(UserId);
        var second = CreateStore(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var bytes = "same content"u8.ToArray();
        var key = CreateKey(bytes);

        var firstPublished = await WriteAndPublishAsync(first, key, bytes);
        var secondPublished = await WriteAndPublishAsync(second, key, bytes);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, firstPublished.Status);
        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, secondPublished.Status);
        Assert.NotEqual(first.Identity.Id, second.Identity.Id);
        Assert.NotEqual(first.ScopeDirectory, second.ScopeDirectory);
        Assert.True(File.Exists(Path.Combine(first.ScopeDirectory, firstPublished.RelativePath!)));
        Assert.True(File.Exists(Path.Combine(second.ScopeDirectory, secondPublished.RelativePath!)));
    }

    [Fact]
    public async Task CreateStagingAsync_WhenReservationsReachQuota_RejectsAndReleaseOnDispose()
    {
        var store = CreateStore(quotaBytes: 4);
        var firstKey = CreateKey("abc"u8.ToArray());
        var first = await store.CreateStagingAsync(
            firstKey.ConversationId,
            firstKey.AttachmentId,
            expectedSize: 3);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, first.Status);
        var secondKey = CreateKey("d"u8.ToArray());
        var blocked = await store.CreateStagingAsync(
            secondKey.ConversationId,
            secondKey.AttachmentId,
            expectedSize: 2);
        Assert.Equal(ClientAttachmentCacheStoreStatus.QuotaExceeded, blocked.Status);

        await first.StagingFile!.DisposeAsync();
        var released = await store.CreateStagingAsync(
            secondKey.ConversationId,
            secondKey.AttachmentId,
            expectedSize: 1);
        await released.StagingFile!.DisposeAsync();

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, released.Status);
    }

    [Fact]
    public async Task PublishAsync_WhenFinalAlreadyExists_DoesNotOverwriteExistingContent()
    {
        var store = CreateStore();
        var bytes = "deduplicated"u8.ToArray();
        var key = CreateKey(bytes);
        var firstStaging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            bytes.LongLength)).StagingFile!;
        var secondStaging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            bytes.LongLength)).StagingFile!;
        await using (firstStaging)
        await using (secondStaging)
        {
            await firstStaging.Stream.WriteAsync(bytes);
            await secondStaging.Stream.WriteAsync(bytes);

            var first = await store.PublishAsync(firstStaging, key.Sha256);
            var second = await store.PublishAsync(secondStaging, key.Sha256);

            Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, first.Status);
            Assert.Equal(ClientAttachmentCacheStoreStatus.AlreadyPublished, second.Status);
            Assert.Equal(first.RelativePath, second.RelativePath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(
                Path.Combine(store.ScopeDirectory, first.RelativePath!)));
        }
    }

    [Fact]
    public async Task PublishAsync_WhenFinalAlreadyExistsButIsCorrupt_ReplacesItAtomically()
    {
        var store = CreateStore();
        var bytes = "trusted replacement"u8.ToArray();
        var key = CreateKey(bytes);
        var relativePath =
            $"{key.ConversationId:N}.{key.AttachmentId:N}.{key.Sha256}.cache";
        Directory.CreateDirectory(store.ScopeDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(store.ScopeDirectory, relativePath),
            new byte[bytes.Length]);

        var published = await WriteAndPublishAsync(store, key, bytes);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, published.Status);
        Assert.Equal(relativePath, published.RelativePath);
        Assert.Equal(
            bytes,
            await File.ReadAllBytesAsync(Path.Combine(store.ScopeDirectory, relativePath)));
    }

    [Fact]
    public async Task CreateStagingAsync_WhenSameScopeUsesTwoStores_SharesQuotaReservations()
    {
        var first = CreateStore(quotaBytes: 4);
        var second = CreateStore(quotaBytes: 4);
        var key = CreateKey("abc"u8.ToArray());
        var firstReservation = await first.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            expectedSize: 3);

        var blocked = await second.CreateStagingAsync(
            key.ConversationId,
            Guid.NewGuid(),
            expectedSize: 2);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, firstReservation.Status);
        Assert.Equal(ClientAttachmentCacheStoreStatus.QuotaExceeded, blocked.Status);
        await firstReservation.StagingFile!.DisposeAsync();
    }

    [Fact]
    public async Task DeleteConversationAsync_WhenManagedFinalAndStagingExist_DeletesOnlyExactConversation()
    {
        var store = CreateStore();
        var firstBytes = "first"u8.ToArray();
        var secondBytes = "second"u8.ToArray();
        var firstKey = CreateKey(firstBytes, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var secondKey = CreateKey(secondBytes, Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var firstPublished = await WriteAndPublishAsync(store, firstKey, firstBytes);
        var secondPublished = await WriteAndPublishAsync(store, secondKey, secondBytes);
        var stagingName = $"{firstKey.ConversationId:N}.{Guid.NewGuid():N}.{Guid.NewGuid():N}.part";
        await File.WriteAllBytesAsync(Path.Combine(store.ScopeDirectory, stagingName), "orphan"u8.ToArray());

        var inventory = await store.EnumerateAsync();
        var deleted = await store.DeleteConversationAsync(firstKey.ConversationId);

        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, inventory.Status);
        Assert.Equal(3, inventory.Entries.Count);
        Assert.Equal(ClientAttachmentCacheStoreStatus.Ready, deleted.Status);
        Assert.Equal(2, deleted.DeletedCount);
        Assert.False(File.Exists(Path.Combine(store.ScopeDirectory, firstPublished.RelativePath!)));
        Assert.False(File.Exists(Path.Combine(store.ScopeDirectory, stagingName)));
        Assert.True(File.Exists(Path.Combine(store.ScopeDirectory, secondPublished.RelativePath!)));
    }

    [Fact]
    public async Task GetQuotaAsync_WhenScopeDirectoryIsReparsePoint_FailsClosed()
    {
        var store = CreateStore();
        var target = Path.Combine(rootDirectory, "junction-target");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(store.CacheRoot);
        Directory.CreateDirectory(target);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", store.ScopeDirectory, target },
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);

        var quota = await store.GetQuotaAsync();

        Assert.Equal(ClientAttachmentCacheStoreStatus.StorageFailure, quota.Status);
    }

    [Fact]
    public async Task ResultsAndModels_WhenFormatted_DoNotRevealPathsOrIdentifiers()
    {
        var store = CreateStore();
        var bytes = "secret content"u8.ToArray();
        var key = CreateKey(bytes);
        var published = await WriteAndPublishAsync(store, key, bytes);
        var entry = (await store.EnumerateAsync()).Entries.Single();
        var resolution = await store.ValidateAndResolveAsync(
            published.RelativePath!,
            key,
            bytes.LongLength);
        using var resolvedFile = resolution.File;

        var formatted = string.Join(
            "\n",
            store,
            key,
            published,
            entry,
            resolution,
            new ClientAttachmentCacheStoreQuotaOutcome(
                ClientAttachmentCacheStoreStatus.Ready,
                1,
                ClientAttachmentCacheStore.DefaultQuotaBytes));

        Assert.DoesNotContain(rootDirectory, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(store.Identity.Id, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(key.ConversationId.ToString("D"), formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key.AttachmentId.ToString("D"), formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key.Sha256, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(published.RelativePath!, formatted, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var cacheRoot = Path.Combine(rootDirectory, "cache");
        if (Directory.Exists(cacheRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
        }

        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private ClientAttachmentCacheStore CreateStore(Guid? userId = null, long? quotaBytes = null) =>
        quotaBytes.HasValue
            ? new ClientAttachmentCacheStore(
                AccountScopeIdentity.Create(ServerBaseUri, userId ?? UserId, rootDirectory),
                Path.Combine(rootDirectory, "cache"),
                quotaBytes.Value)
            : new ClientAttachmentCacheStore(
                AccountScopeIdentity.Create(ServerBaseUri, userId ?? UserId, rootDirectory),
                Path.Combine(rootDirectory, "cache"));

    private static ClientAttachmentCacheStoreKey CreateKey(
        byte[] bytes,
        Guid? conversationId = null) =>
        new(
            conversationId ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static async Task<ClientAttachmentCacheStorePublishOutcome> WriteAndPublishAsync(
        ClientAttachmentCacheStore store,
        ClientAttachmentCacheStoreKey key,
        byte[] bytes)
    {
        var staging = (await store.CreateStagingAsync(
            key.ConversationId,
            key.AttachmentId,
            bytes.LongLength)).StagingFile!;
        await using (staging)
        {
            await staging.Stream.WriteAsync(bytes);
            return await store.PublishAsync(staging, key.Sha256);
        }
    }
}
