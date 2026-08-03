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
