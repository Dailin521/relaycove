using RelayCove.Client.Accounts;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientMessageCopyPolicyTests
{
    [Fact]
    public void TryResolveContent_WhenItemBelongsToCurrentReadySnapshot_ReturnsExactContent()
    {
        var conversationId = Guid.NewGuid();
        const string content = "  first line\r\nsecond line  ";
        var item = CreateItem(conversationId, content);
        var snapshot = CreateSnapshot(
            ClientMessageListStatus.Ready,
            conversationId,
            [item]);

        var resolved = ClientMessageCopyPolicy.TryResolveContent(
            snapshot,
            item,
            out var copiedContent);

        Assert.True(resolved);
        Assert.Equal(content, copiedContent);
    }

    [Fact]
    public void TryResolveContent_WhenItemIsStaleOrSnapshotIsNotReady_RejectsContent()
    {
        var conversationId = Guid.NewGuid();
        var item = CreateItem(conversationId, "current content");
        var ready = CreateSnapshot(
            ClientMessageListStatus.Ready,
            conversationId,
            [item]);
        var notReady = CreateSnapshot(
            ClientMessageListStatus.RevokedConversation,
            conversationId,
            [item]);

        Assert.False(ClientMessageCopyPolicy.TryResolveContent(
            ready,
            item with { Content = "stale content" },
            out var staleContent));
        Assert.Equal(string.Empty, staleContent);
        Assert.False(ClientMessageCopyPolicy.TryResolveContent(
            notReady,
            item,
            out var revokedContent));
        Assert.Equal(string.Empty, revokedContent);
        Assert.False(ClientMessageCopyPolicy.TryResolveContent(
            ready,
            item with { CanCopy = false },
            out var disabledContent));
        Assert.Equal(string.Empty, disabledContent);
    }

    private static ClientMessageListItemPresentation CreateItem(
        Guid conversationId,
        string content)
    {
        var message = new MessageDto(
            10,
            Guid.NewGuid(),
            conversationId,
            Guid.NewGuid(),
            "Sender",
            MessageType.Text,
            content,
            ReplyToMessageId: null,
            Array.Empty<AttachmentDto>(),
            Array.Empty<Guid>(),
            DateTimeOffset.Parse("2026-08-03T01:00:00Z"));
        return Assert.Single(ClientMessageListPresenter.Present(
            [message],
            currentUserId: Guid.NewGuid()));
    }

    private static ClientMessageListSnapshot CreateSnapshot(
        ClientMessageListStatus status,
        Guid conversationId,
        IReadOnlyList<ClientMessageListItemPresentation> items) =>
        new(
            status,
            conversationId,
            items,
            IsLoading: false,
            HasMoreBefore: false,
            HasMoreAfter: false,
            TargetMessageId: null,
            LastLoadStatus: null,
            Revision: 1);
}
