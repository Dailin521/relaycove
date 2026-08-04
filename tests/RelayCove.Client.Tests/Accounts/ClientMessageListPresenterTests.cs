using RelayCove.Client.Accounts;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientMessageListPresenterTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void Present_WhenPendingRowsExist_AppendsWithoutServerIdsAndExposesRetryState()
    {
        var conversationId = Guid.NewGuid();
        var confirmed = new MessageDto(
            10,
            Guid.NewGuid(),
            conversationId,
            UserId,
            "Sender",
            MessageType.Text,
            "confirmed",
            ReplyToMessageId: null,
            Array.Empty<AttachmentDto>(),
            Array.Empty<Guid>(),
            DateTimeOffset.Parse("2026-08-03T01:00:00Z"));
        var sending = CreatePending(1, conversationId, MessageSendStatus.Sending, "sending");
        var failed = CreatePending(2, conversationId, MessageSendStatus.Failed, "failed");

        var items = ClientMessageListPresenter.Present(
            [confirmed],
            [failed, sending],
            UserId);

        Assert.Equal(3, items.Count);
        Assert.Equal(10, items[0].ServerMessageId);
        Assert.Equal(MessageSendStatus.Sent, items[0].SendStatus);
        Assert.True(items[0].CanReply);
        Assert.Null(items[1].ServerMessageId);
        Assert.Equal(sending.ClientMessageId, items[1].ClientMessageId);
        Assert.Equal("发送中…", items[1].SendStatusLabel);
        Assert.False(items[1].CanRetry);
        Assert.False(items[1].CanReply);
        Assert.Null(items[2].ServerMessageId);
        Assert.Equal(failed.ClientMessageId, items[2].ClientMessageId);
        Assert.Equal("发送失败", items[2].SendStatusLabel);
        Assert.True(items[2].CanRetry);
        Assert.False(items[2].CanReply);
        Assert.DoesNotContain("failed", items[2].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(failed.ClientMessageId.ToString(), items[2].ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Present_WhenRepliesExist_ResolvesLoadedTargetsAndMarksMissingTargetsHonestly()
    {
        var conversationId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var target = CreateMessage(
            73,
            conversationId,
            otherUserId,
            "Target Sender",
            "sensitive target",
            replyToMessageId: null);
        var loadedReply = CreateMessage(
            74,
            conversationId,
            UserId,
            "Current User",
            "loaded reply",
            replyToMessageId: 73);
        var missingReply = CreateMessage(
            75,
            conversationId,
            otherUserId,
            "Other",
            "missing reply",
            replyToMessageId: 999_999);
        var pendingReply = CreatePending(
            1,
            conversationId,
            MessageSendStatus.Sending,
            "pending reply",
            replyToMessageId: 73);

        var items = ClientMessageListPresenter.Present(
            [missingReply, loadedReply, target],
            [pendingReply],
            UserId);

        var loaded = Assert.Single(items, item => item.ServerMessageId == 74);
        Assert.True(loaded.HasReply);
        Assert.True(loaded.IsReplyTargetAvailable);
        Assert.Equal(73, loaded.ReplyToMessageId);
        Assert.Equal("回复 Target Sender", loaded.ReplySenderLabel);
        Assert.Equal("sensitive target", loaded.ReplyContent);
        var missing = Assert.Single(items, item => item.ServerMessageId == 75);
        Assert.True(missing.HasReply);
        Assert.False(missing.IsReplyTargetAvailable);
        Assert.Equal("回复消息", missing.ReplySenderLabel);
        Assert.Equal("原消息未加载，点击定位", missing.ReplyContent);
        var pending = Assert.Single(items, item => item.ServerMessageId is null);
        Assert.True(pending.HasReply);
        Assert.True(pending.IsReplyTargetAvailable);
        Assert.False(pending.CanReply);
        Assert.Equal("回复 Target Sender", pending.ReplySenderLabel);
        Assert.Equal("sensitive target", pending.ReplyContent);
        Assert.DoesNotContain("sensitive target", loaded.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("999999", missing.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Target Sender", pending.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Present_WhenLocalDateChanges_ShowsOnlyFirstAndChangedDateSeparators()
    {
        var conversationId = Guid.NewGuid();
        var firstLocalDateTime = new DateTime(
            2026,
            8,
            3,
            10,
            0,
            0,
            DateTimeKind.Unspecified);
        var firstLocal = new DateTimeOffset(
            firstLocalDateTime,
            TimeZoneInfo.Local.GetUtcOffset(firstLocalDateTime));
        var first = CreateMessage(
            10,
            conversationId,
            UserId,
            "Sender",
            "first",
            replyToMessageId: null) with
        {
            CreatedAt = firstLocal,
        };
        var sameDay = CreateMessage(
            11,
            conversationId,
            UserId,
            "Sender",
            "same day",
            replyToMessageId: null) with
        {
            CreatedAt = firstLocal.AddHours(1),
        };
        var nextDayPending = CreatePending(
            1,
            conversationId,
            MessageSendStatus.Sending,
            "next day") with
        {
            CreatedAt = firstLocal.AddDays(1),
        };
        var samePendingDay = CreatePending(
            2,
            conversationId,
            MessageSendStatus.Failed,
            "same pending day") with
        {
            CreatedAt = firstLocal.AddDays(1).AddHours(1),
        };

        var items = ClientMessageListPresenter.Present(
            [sameDay, first],
            [samePendingDay, nextDayPending],
            UserId);

        Assert.Equal(4, items.Count);
        Assert.True(items[0].ShowDateSeparator);
        Assert.Equal("2026-08-03", items[0].DateSeparatorLabel);
        Assert.False(items[1].ShowDateSeparator);
        Assert.Equal("2026-08-03", items[1].DateSeparatorLabel);
        Assert.True(items[2].ShowDateSeparator);
        Assert.Equal("2026-08-04", items[2].DateSeparatorLabel);
        Assert.False(items[3].ShowDateSeparator);
        Assert.Equal("2026-08-04", items[3].DateSeparatorLabel);
        Assert.All(items, item => Assert.True(item.CanCopy));
        Assert.DoesNotContain("2026-08-03", items[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Present_WhenConfirmedAndPendingContainLinks_ExposesRedactedSafeLinks()
    {
        var conversationId = Guid.NewGuid();
        var confirmed = CreateMessage(
            10,
            conversationId,
            UserId,
            "Sender",
            "confirmed https://confirmed.example/path",
            replyToMessageId: null);
        var pending = CreatePending(
            1,
            conversationId,
            MessageSendStatus.Sending,
            "pending http://pending.example/path");

        var items = ClientMessageListPresenter.Present(
            [confirmed],
            [pending],
            UserId);

        Assert.All(items, item => Assert.True(item.HasLinks));
        Assert.Equal(
            "https://confirmed.example/path",
            Assert.Single(items[0].Links).AbsoluteUri);
        Assert.Equal(
            "http://pending.example/path",
            Assert.Single(items[1].Links).AbsoluteUri);
        Assert.DoesNotContain("confirmed.example", items[0].ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending.example", items[1].ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Present_WhenNewMessageTargetIsConfirmedOtherMessage_ShowsExactlyOneSeparator()
    {
        var conversationId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var firstOther = CreateMessage(
            10,
            conversationId,
            otherUserId,
            "Other",
            "first other",
            replyToMessageId: null);
        var own = CreateMessage(
            11,
            conversationId,
            UserId,
            "Current User",
            "own",
            replyToMessageId: null);
        var target = CreateMessage(
            12,
            conversationId,
            otherUserId,
            "Other",
            "target",
            replyToMessageId: null);
        var pending = CreatePending(
            1,
            conversationId,
            MessageSendStatus.Sending,
            "pending");

        var items = ClientMessageListPresenter.Present(
            [target, own, firstOther],
            [pending],
            UserId,
            newMessageSeparatorBeforeMessageId: 12);

        var separator = Assert.Single(items, item => item.ShowNewMessageSeparator);
        Assert.Equal(12, separator.ServerMessageId);
        Assert.False(items[^1].ShowNewMessageSeparator);
        Assert.Contains("ShowNewMessageSeparator = True", separator.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Present_WhenNewMessageTargetIsOwnOrInvalid_DoesNotShowSeparator()
    {
        var conversationId = Guid.NewGuid();
        var own = CreateMessage(
            11,
            conversationId,
            UserId,
            "Current User",
            "own",
            replyToMessageId: null);

        var items = ClientMessageListPresenter.Present(
            [own],
            Array.Empty<LocalPendingMessage>(),
            UserId,
            newMessageSeparatorBeforeMessageId: 11);

        Assert.False(Assert.Single(items).ShowNewMessageSeparator);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClientMessageListPresenter.Present(
                [own],
                Array.Empty<LocalPendingMessage>(),
                UserId,
                newMessageSeparatorBeforeMessageId: 0));
    }

    [Fact]
    public void Present_WhenConfirmedAttachmentsExist_ProjectsSafeOrderedMetadataAndDownloadState()
    {
        var conversationId = Guid.NewGuid();
        var firstAttachment = CreateAttachment(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "safe-image.png",
            "image/png",
            1024);
        var secondAttachment = CreateAttachment(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "safe-document.pdf",
            "application/pdf",
            1536);
        var confirmed = new MessageDto(
            100,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            conversationId,
            UserId,
            "Sender",
            MessageType.File,
            Content: null,
            ReplyToMessageId: null,
            Attachments: [firstAttachment, secondAttachment],
            MentionUserIds: Array.Empty<Guid>(),
            CreatedAt: DateTimeOffset.Parse("2026-08-03T01:00:00Z"));
        var pending = new LocalPendingMessage(
            1,
            Guid.NewGuid(),
            conversationId,
            UserId,
            "Sender",
            MessageType.Image,
            Content: null,
            ReplyToMessageId: null,
            MentionUserIds: Array.Empty<Guid>(),
            CreatedAt: DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
            MessageSendStatus.Sending)
        {
            AttachmentIds = [firstAttachment.Id],
        };

        var items = ClientMessageListPresenter.Present(
            [confirmed],
            [pending],
            UserId,
            downloadedAttachmentIds: new HashSet<Guid> { secondAttachment.Id });

        var confirmedItem = Assert.Single(items, item => item.ServerMessageId == confirmed.Id);
        Assert.True(confirmedItem.HasAttachments);
        Assert.Collection(
            confirmedItem.Attachments,
            first =>
            {
                Assert.Equal(confirmed.ClientMessageId, first.MessageClientId);
                Assert.Equal(firstAttachment.Id, first.AttachmentId);
                Assert.Equal("safe-image.png", first.DisplayName);
                Assert.Equal("1 KiB", first.DisplaySize);
                Assert.True(first.IsImage);
                Assert.False(first.IsDownloaded);
            },
            second =>
            {
                Assert.Equal(confirmed.ClientMessageId, second.MessageClientId);
                Assert.Equal(secondAttachment.Id, second.AttachmentId);
                Assert.Equal("safe-document.pdf", second.DisplayName);
                Assert.Equal("1.5 KiB", second.DisplaySize);
                Assert.False(second.IsImage);
                Assert.True(second.IsDownloaded);
            });
        var pendingItem = Assert.Single(items, item => item.ServerMessageId is null);
        Assert.False(pendingItem.HasAttachments);
        Assert.Empty(pendingItem.Attachments);
        Assert.DoesNotContain("safe-image.png", confirmedItem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(confirmed.ClientMessageId.ToString(), confirmedItem.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondAttachment.Id.ToString(), confirmedItem.Attachments[1].ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static MessageDto CreateMessage(
        long id,
        Guid conversationId,
        Guid senderId,
        string senderDisplayName,
        string content,
        long? replyToMessageId) =>
        new(
            id,
            Guid.NewGuid(),
            conversationId,
            senderId,
            senderDisplayName,
            MessageType.Text,
            content,
            replyToMessageId,
            Array.Empty<AttachmentDto>(),
            Array.Empty<Guid>(),
            DateTimeOffset.Parse("2026-08-03T01:00:00Z").AddMinutes(id));

    private static AttachmentDto CreateAttachment(
        Guid id,
        string fileName,
        string contentType,
        long size) =>
        new(
            id,
            fileName,
            contentType,
            size,
            $"/api/attachments/{id:D}/download",
            ThumbnailUrl: null);

    private static LocalPendingMessage CreatePending(
        long localId,
        Guid conversationId,
        MessageSendStatus status,
        string content,
        long? replyToMessageId = null) =>
        new(
            localId,
            Guid.NewGuid(),
            conversationId,
            UserId,
            "Sender",
            MessageType.Text,
            content,
            replyToMessageId,
            Array.Empty<Guid>(),
            DateTimeOffset.Parse("2026-08-03T02:00:00Z").AddMinutes(localId),
            status);
}
