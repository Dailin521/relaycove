using RelayCove.Client.Accounts;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientMessageLinkPolicyTests
{
    [Fact]
    public void IsCurrent_WhenLinkBelongsToReadySnapshot_ReturnsTrue()
    {
        var conversationId = Guid.NewGuid();
        var item = CreateItem(conversationId);
        var snapshot = CreateSnapshot(ClientMessageListStatus.Ready, conversationId, [item]);

        Assert.True(ClientMessageLinkPolicy.IsCurrent(snapshot, Assert.Single(item.Links)));
    }

    [Fact]
    public void IsCurrent_WhenLinkIsStaleOrSnapshotIsNotReady_ReturnsFalse()
    {
        var conversationId = Guid.NewGuid();
        var item = CreateItem(conversationId);
        var link = Assert.Single(item.Links);
        var ready = CreateSnapshot(ClientMessageListStatus.Ready, conversationId, [item]);
        var revoked = CreateSnapshot(
            ClientMessageListStatus.RevokedConversation,
            conversationId,
            [item]);

        Assert.False(ClientMessageLinkPolicy.IsCurrent(
            ready,
            link with { AbsoluteUri = "https://stale.test/" }));
        Assert.False(ClientMessageLinkPolicy.IsCurrent(revoked, link));
    }

    private static ClientMessageListItemPresentation CreateItem(Guid conversationId)
    {
        var message = new MessageDto(
            10,
            Guid.NewGuid(),
            conversationId,
            Guid.NewGuid(),
            "Sender",
            MessageType.Text,
            "open https://example.test/path",
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
