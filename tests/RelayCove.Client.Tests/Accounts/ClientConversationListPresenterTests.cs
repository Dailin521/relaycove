using RelayCove.Client.Accounts;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientConversationListPresenterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-03T12:00:00Z");

    [Fact]
    public void Present_WhenMessagesVary_UsesBoundedExplicitPreviews()
    {
        var text = new string('a', 120) + "\r\nsecret-tail";
        var items = new[]
        {
            CreateItem(MessageType.Text, text),
            CreateItem(MessageType.Image, null) with { Id = Guid.NewGuid() },
            CreateItem(MessageType.File, null) with { Id = Guid.NewGuid() },
            CreateItem(null, null) with { Id = Guid.NewGuid() },
            CreateItem(MessageType.Text, null) with { Id = Guid.NewGuid() },
        };
        var outcome = new LocalConversationListReadOutcome(
            LocalCacheOperationStatus.Ready,
            items,
            TotalUnreadCount: 3,
            Revision: 4);

        var presentation = ClientConversationListPresenter.Present(outcome);

        Assert.Equal(5, presentation.Count);
        Assert.EndsWith("…", presentation[0].Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tail", presentation[0].Preview, StringComparison.Ordinal);
        Assert.Equal("[图片]", presentation[1].Preview);
        Assert.Equal("[文件]", presentation[2].Preview);
        Assert.Equal("正在同步消息…", presentation[3].Preview);
        Assert.Equal("[空文本消息]", presentation[4].Preview);
        Assert.Equal("99+", presentation[0].UnreadText);
        Assert.True(presentation[0].HasUnread);
        Assert.Equal("已静音", presentation[0].MutedLabel);
        Assert.DoesNotContain(text, presentation[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Present_WhenOutcomeIsNotReady_ReturnsNoRows()
    {
        var outcome = LocalConversationListReadOutcome.Failure(
            LocalCacheOperationStatus.FatalScope,
            revision: 8);

        var presentation = ClientConversationListPresenter.Present(outcome);

        Assert.Empty(presentation);
    }

    [Fact]
    public void ResolveSelection_WhenPendingTargetIsMissingFromReadyList_ExpiresAndRestoresPrevious()
    {
        var previousId = Guid.NewGuid();
        var items = ClientConversationListPresenter.Present(new LocalConversationListReadOutcome(
            LocalCacheOperationStatus.Ready,
            [CreateItem(MessageType.Text, "previous") with { Id = previousId }],
            TotalUnreadCount: 0,
            Revision: 1));

        var resolution = ClientConversationListPresenter.ResolveSelection(
            items,
            LocalCacheOperationStatus.Ready,
            pendingSelectionId: Guid.NewGuid(),
            previousId);

        Assert.True(resolution.ClearPendingSelection);
        Assert.Equal(previousId, resolution.Selection?.Id);
    }

    [Fact]
    public void ResolveSelection_WhenPendingTargetMayStillArrive_RetainsIt()
    {
        var resolution = ClientConversationListPresenter.ResolveSelection(
            Array.Empty<ClientConversationListItemPresentation>(),
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            pendingSelectionId: Guid.NewGuid(),
            previousSelectionId: null);

        Assert.False(resolution.ClearPendingSelection);
        Assert.Null(resolution.Selection);
    }

    [Fact]
    public void ResolveSelection_WhenPendingTargetExists_SelectsAndConsumesIt()
    {
        var pendingId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var items = ClientConversationListPresenter.Present(new LocalConversationListReadOutcome(
            LocalCacheOperationStatus.Ready,
            [
                CreateItem(MessageType.Text, "previous") with { Id = previousId },
                CreateItem(MessageType.Text, "pending") with { Id = pendingId },
            ],
            TotalUnreadCount: 0,
            Revision: 1));

        var resolution = ClientConversationListPresenter.ResolveSelection(
            items,
            LocalCacheOperationStatus.Ready,
            pendingId,
            previousId);

        Assert.True(resolution.ClearPendingSelection);
        Assert.Equal(pendingId, resolution.Selection?.Id);
    }

    private static LocalConversationListItem CreateItem(
        MessageType? messageType,
        string? content) =>
        new(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Private",
            null,
            10,
            messageType,
            content,
            Now,
            120,
            true,
            Now);
}
