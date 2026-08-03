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
        Assert.Null(items[1].ServerMessageId);
        Assert.Equal(sending.ClientMessageId, items[1].ClientMessageId);
        Assert.Equal("发送中…", items[1].SendStatusLabel);
        Assert.False(items[1].CanRetry);
        Assert.Null(items[2].ServerMessageId);
        Assert.Equal(failed.ClientMessageId, items[2].ClientMessageId);
        Assert.Equal("发送失败", items[2].SendStatusLabel);
        Assert.True(items[2].CanRetry);
        Assert.DoesNotContain("failed", items[2].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(failed.ClientMessageId.ToString(), items[2].ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static LocalPendingMessage CreatePending(
        long localId,
        Guid conversationId,
        MessageSendStatus status,
        string content) =>
        new(
            localId,
            Guid.NewGuid(),
            conversationId,
            UserId,
            "Sender",
            MessageType.Text,
            content,
            ReplyToMessageId: null,
            Array.Empty<Guid>(),
            DateTimeOffset.Parse("2026-08-03T02:00:00Z").AddMinutes(localId),
            status);
}
