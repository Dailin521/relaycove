using RelayCove.Client.Mentions;

namespace RelayCove.Client.Tests.Mentions;

public sealed class ClientTextComposerContextPolicyTests
{
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ShouldClearCommittedDraft_WhenPendingAndVisibleDraftIsUnchanged_ReturnsTrue()
    {
        Assert.True(ClientTextComposerContextPolicy.ShouldClearCommittedDraft(
            pendingCommitted: true,
            ConversationId,
            ConversationId,
            "hello",
            "hello",
            replyContextUnchanged: true,
            mentionContextUnchanged: true));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ShouldClearCommittedDraft_WhenSafetyGateChanges_ReturnsFalse(
        bool pendingCommitted,
        bool sameConversation,
        bool sameContent,
        bool sameReplyAndMentions)
    {
        Assert.False(ClientTextComposerContextPolicy.ShouldClearCommittedDraft(
            pendingCommitted,
            ConversationId,
            sameConversation ? ConversationId : Guid.NewGuid(),
            "hello",
            sameContent ? "hello" : "hello again",
            replyContextUnchanged: sameReplyAndMentions,
            mentionContextUnchanged: sameReplyAndMentions));
    }
}
