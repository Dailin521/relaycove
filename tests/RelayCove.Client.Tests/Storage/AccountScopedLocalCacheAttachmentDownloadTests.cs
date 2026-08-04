using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

[Collection(SqliteTestCollection.Name)]
public sealed class AccountScopedLocalCacheAttachmentDownloadTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AttachmentDownload_WhenConfirmed_ClaimsCompletesAndTransitionsDeterministically()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var attachment = await AddConfirmedAttachmentAsync(cache, conversation);

        var initialClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);
        var duplicateClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);
        var failed = await cache.FailAttachmentDownloadAsync(
            conversation.Id,
            attachment.Id,
            canceled: false);
        Assert.Equal(LocalCacheOperationStatus.Ready, initialClaim.Status);
        Assert.Equal(LocalAttachmentDownloadClaimResult.Claimed, initialClaim.Result);
        Assert.Equal(LocalAttachmentDownloadState.Downloading, initialClaim.Record!.State);
        Assert.Equal(LocalAttachmentDownloadClaimResult.InProgress, duplicateClaim.Result);
        Assert.Equal(LocalAttachmentDownloadState.Downloading, duplicateClaim.Record!.State);
        Assert.Equal(LocalCacheOperationStatus.Ready, failed);
        Assert.Equal(3, Scalar(identity, "SELECT DownloadStatus FROM LocalAttachments;"));

        var retryClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);
        var managedPath = ManagedPath(conversation.Id, attachment.Id);
        var completed = await cache.CompleteAttachmentDownloadAsync(
            conversation.Id,
            attachment.Id,
            managedPath);
        var cachedClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);

        Assert.Equal(LocalAttachmentDownloadClaimResult.Claimed, retryClaim.Result);
        Assert.Equal(LocalCacheOperationStatus.Ready, completed);
        Assert.Equal(LocalAttachmentDownloadClaimResult.AlreadyDownloaded, cachedClaim.Result);
        Assert.Equal(LocalAttachmentDownloadState.Downloaded, cachedClaim.Record!.State);
        Assert.Equal(managedPath, cachedClaim.Record.LocalPath);

        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.InvalidateDownloadedAttachmentAsync(
                conversation.Id,
                attachment.Id,
                managedPath));
        Assert.Equal(0, Scalar(identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        Assert.Null(TextScalarOrNull(identity, "SELECT LocalPath FROM LocalAttachments;"));
    }

    [Fact]
    public async Task AttachmentDownload_WhenCanceled_ReturnsToNotDownloaded()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var attachment = await AddConfirmedAttachmentAsync(cache, conversation);
        await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);

        var status = await cache.FailAttachmentDownloadAsync(
            conversation.Id,
            attachment.Id,
            canceled: true);

        Assert.Equal(LocalCacheOperationStatus.Ready, status);
        Assert.Equal(0, Scalar(identity, "SELECT DownloadStatus FROM LocalAttachments;"));

        var nextClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);
        Assert.Equal(LocalAttachmentDownloadClaimResult.Claimed, nextClaim.Result);
    }

    [Fact]
    public async Task ReadMessagePageAsync_WhenPageAttachmentIsDownloaded_ProjectsOnlyDownloadedIds()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var downloaded = CreateAttachment(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var notDownloaded = CreateAttachment(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        await AddConfirmedAttachmentsAsync(cache, conversation, [downloaded, notDownloaded]);
        Assert.Equal(
            LocalAttachmentDownloadClaimResult.Claimed,
            (await cache.ClaimAttachmentDownloadAsync(conversation.Id, downloaded.Id)).Result);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.CompleteAttachmentDownloadAsync(
                conversation.Id,
                downloaded.Id,
                ManagedPath(conversation.Id, downloaded.Id)));

        var page = await cache.ReadMessagePageAsync(conversation.Id, beforeMessageId: null, limit: 20);

        Assert.Equal(LocalCacheOperationStatus.Ready, page.Status);
        Assert.Contains(downloaded.Id, page.DownloadedAttachmentIds);
        Assert.DoesNotContain(notDownloaded.Id, page.DownloadedAttachmentIds);
    }

    [Fact]
    public async Task AttachmentDownload_WhenAttachmentIsWrongConversationUnboundOrUnconfirmed_IsUnavailable()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var otherConversation = CreateConversation();
        var confirmed = await AddConfirmedAttachmentAsync(cache, conversation);
        await ApplySnapshotAsync(cache, [conversation, otherConversation]);
        var unbound = CreateAttachment();
        var unconfirmed = CreateAttachment();

        Assert.Equal(
            LocalAttachmentReservationResult.Stored,
            (await cache.StoreUnboundAttachmentReservationAsync(unbound)).Result);
        Assert.Equal(
            LocalAttachmentReservationResult.Stored,
            (await cache.StoreUnboundAttachmentReservationAsync(unconfirmed)).Result);
        var pending = new PendingMessage(
            Guid.NewGuid(),
            conversation.Id,
            UserId,
            "Current user",
            MessageType.Image,
            Content: null,
            ReplyToMessageId: null,
            MentionUserIds: Array.Empty<Guid>(),
            CreatedAt: DateTimeOffset.Parse("2026-08-04T03:00:00Z"))
        {
            AttachmentIds = [unconfirmed.Id],
        };
        Assert.Equal(
            LocalPendingMessageMutationResult.Created,
            (await cache.CreatePendingMessageAsync(pending)).Result);

        var wrongConversation = await cache.ClaimAttachmentDownloadAsync(
            otherConversation.Id,
            confirmed.Id);
        var unboundClaim = await cache.ClaimAttachmentDownloadAsync(conversation.Id, unbound.Id);
        var unconfirmedClaim = await cache.ClaimAttachmentDownloadAsync(
            conversation.Id,
            unconfirmed.Id);

        Assert.All(
            new[] { wrongConversation, unboundClaim, unconfirmedClaim },
            outcome =>
            {
                Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
                Assert.Equal(LocalAttachmentDownloadClaimResult.AttachmentUnavailable, outcome.Result);
                Assert.Null(outcome.Record);
            });
    }

    [Fact]
    public async Task AttachmentDownload_WhenRevoked_PublishesCancellationAndDurablePurge()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        await AddConfirmedAttachmentAsync(cache, conversation);
        var cancellations = new List<Guid>();
        var purges = new List<Guid>();
        cache.AttachmentDownloadCancellationRequested += cancellations.Add;
        cache.AttachmentCachePurged += conversationId =>
        {
            purges.Add(conversationId);
            return Task.CompletedTask;
        };

        var status = await cache.RevokeConversationAccessAsync(conversation.Id);

        Assert.Equal(LocalCacheOperationStatus.RevokedConversation, status);
        Assert.Equal([conversation.Id], cancellations);
        Assert.Equal([conversation.Id], purges);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalAttachments;"));
    }

    [Fact]
    public async Task AuthoritativeSnapshot_WhenRevokedConversationRejoins_DoesNotRepurgeAttachments()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        await AddConfirmedAttachmentAsync(cache, conversation);
        var purges = new List<Guid>();
        cache.AttachmentCachePurged += conversationId =>
        {
            purges.Add(conversationId);
            return Task.CompletedTask;
        };

        var revoked = await cache.ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
            new ConversationListResponse([], Complete: true));
        Assert.Equal([conversation.Id], revoked.AttachmentPurgeConversationIds);
        Assert.Equal([conversation.Id], purges);

        purges.Clear();
        var rejoined = await cache.ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
            new ConversationListResponse([conversation], Complete: true));
        var notificationRetry = await cache
            .ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
                new ConversationListResponse([conversation], Complete: true));

        Assert.Equal([conversation.Id], rejoined.RevokedConversationIds);
        Assert.Empty(rejoined.AttachmentPurgeConversationIds);
        Assert.Equal([conversation.Id], notificationRetry.RevokedConversationIds);
        Assert.Empty(notificationRetry.AttachmentPurgeConversationIds);
        Assert.Empty(purges);
    }

    [Fact]
    public async Task PrepareAttachmentCacheRecoveryAsync_ResetsInterruptedDownloadAndReturnsOnlyDownloaded()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var first = CreateAttachment(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var second = CreateAttachment(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        await AddConfirmedAttachmentsAsync(cache, conversation, [first, second]);
        Assert.Equal(
            LocalAttachmentDownloadClaimResult.Claimed,
            (await cache.ClaimAttachmentDownloadAsync(conversation.Id, first.Id)).Result);
        Assert.Equal(
            LocalAttachmentDownloadClaimResult.Claimed,
            (await cache.ClaimAttachmentDownloadAsync(conversation.Id, second.Id)).Result);
        var secondPath = ManagedPath(conversation.Id, second.Id);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.CompleteAttachmentDownloadAsync(conversation.Id, second.Id, secondPath));

        var overlappingRecovery = await cache.PrepareAttachmentCacheRecoveryAsync();
        Assert.Equal(LocalCacheOperationStatus.Conflict, overlappingRecovery.Status);

        await cache.DisposeAsync();
        await using var restartedCache = await CreateCacheAsync(identity);
        var recovery = await restartedCache.PrepareAttachmentCacheRecoveryAsync();

        Assert.Equal(LocalCacheOperationStatus.Ready, recovery.Status);
        var recovered = Assert.Single(recovery.DownloadedAttachments);
        Assert.Equal(second.Id, recovered.Attachment.Id);
        Assert.Equal(LocalAttachmentDownloadState.Downloaded, recovered.State);
        Assert.Equal(secondPath, recovered.LocalPath);
        Assert.Equal(0, Scalar(
            identity,
            $"SELECT DownloadStatus FROM LocalAttachments WHERE Id = '{first.Id:D}';"));

        await ApplySnapshotAsync(restartedCache, conversation);
        var retriedFirst = await restartedCache
            .ClaimAttachmentDownloadAsync(conversation.Id, first.Id);
        Assert.Equal(LocalAttachmentDownloadClaimResult.Claimed, retriedFirst.Result);
    }

    [Fact]
    public async Task AttachmentDownload_WhenManagedPathIsInvalidOrMismatched_RejectsBeforeMutation()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var attachment = await AddConfirmedAttachmentAsync(cache, conversation);
        await cache.ClaimAttachmentDownloadAsync(conversation.Id, attachment.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.CompleteAttachmentDownloadAsync(
            conversation.Id,
            attachment.Id,
            $"{conversation.Id:N}.{Guid.NewGuid():N}.{new string('a', 64)}.cache"));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.CompleteAttachmentDownloadAsync(
            conversation.Id,
            attachment.Id,
            $"nested/{ManagedPath(conversation.Id, attachment.Id)}"));

        Assert.Equal(1, Scalar(identity, "SELECT DownloadStatus FROM LocalAttachments;"));
        Assert.Null(TextScalarOrNull(identity, "SELECT LocalPath FROM LocalAttachments;"));
    }

    [Fact]
    public void AttachmentDownloadModels_ToString_RedactsMetadataAndPaths()
    {
        var conversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var attachment = CreateAttachment(Guid.Parse("22222222-2222-2222-2222-222222222222")) with
        {
            OriginalFileName = "private-photo.png",
            ContentType = "image/png",
        };
        var path = ManagedPath(conversationId, attachment.Id);
        var record = new LocalAttachmentDownloadRecord(
            conversationId,
            attachment,
            LocalAttachmentDownloadState.Downloaded,
            path);
        var claim = new LocalAttachmentDownloadClaimOutcome(
            LocalCacheOperationStatus.Ready,
            LocalAttachmentDownloadClaimResult.AlreadyDownloaded,
            record);
        var recovery = new LocalAttachmentCacheRecoveryOutcome(
            LocalCacheOperationStatus.Ready,
            [record]);
        var rendered = string.Join(' ', record, claim, recovery);

        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(conversationId.ToString("D"), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attachment.Id.ToString("D"), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attachment.OriginalFileName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(attachment.ContentType, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(attachment.DownloadUrl, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(path, rendered, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private AccountScopeIdentity CreateIdentity() => AccountScopeIdentity.Create(
        new Uri("https://relaycove.example/team/"),
        UserId,
        rootDirectory);

    private static Task<AccountScopedLocalCache> CreateCacheAsync(AccountScopeIdentity identity) =>
        AccountScopedLocalCache.CreateAsync(identity, NullLogger<AccountScopedLocalCache>.Instance);

    private static async Task<AttachmentDto> AddConfirmedAttachmentAsync(
        AccountScopedLocalCache cache,
        ConversationDto conversation)
    {
        var attachment = CreateAttachment();
        await AddConfirmedAttachmentsAsync(cache, conversation, [attachment]);
        return attachment;
    }

    private static async Task AddConfirmedAttachmentsAsync(
        AccountScopedLocalCache cache,
        ConversationDto conversation,
        IReadOnlyList<AttachmentDto> attachments)
    {
        await ApplySnapshotAsync(cache, conversation);
        var message = new MessageDto(
            101,
            Guid.NewGuid(),
            conversation.Id,
            Guid.NewGuid(),
            "Sender",
            MessageType.Image,
            Content: null,
            ReplyToMessageId: null,
            Attachments: attachments,
            MentionUserIds: Array.Empty<Guid>(),
            CreatedAt: DateTimeOffset.Parse("2026-08-04T02:00:00Z"));
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await cache.MergeIncomingMessageAsync(message)).Result);
    }

    private static async Task ApplySnapshotAsync(
        AccountScopedLocalCache cache,
        ConversationDto conversation) =>
        await ApplySnapshotAsync(cache, [conversation]);

    private static async Task ApplySnapshotAsync(
        AccountScopedLocalCache cache,
        IReadOnlyList<ConversationDto> conversations) =>
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse(conversations, Complete: true)));

    private static ConversationDto CreateConversation() => new(
        Guid.NewGuid(),
        ConversationType.PrivateChannel,
        "Private channel",
        null,
        DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-04T01:00:00Z"),
        101,
        100,
        1);

    private static AttachmentDto CreateAttachment(Guid? attachmentId = null)
    {
        var id = attachmentId ?? Guid.NewGuid();
        return new AttachmentDto(
            id,
            "safe-image.png",
            "image/png",
            1024,
            $"/api/attachments/{id:D}/download",
            ThumbnailUrl: null);
    }

    private static string ManagedPath(Guid conversationId, Guid attachmentId) =>
        $"{conversationId:N}.{attachmentId:N}.{new string('a', 64)}.cache";

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
}
