using RelayCove.Client.Attachments;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentComposerContextPolicyTests
{
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FirstDraftId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondDraftId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void IsCurrent_WhenConversationVersionAndDraftSequenceMatch_ReturnsTrue()
    {
        var result = ClientAttachmentComposerContextPolicy.IsCurrent(
            ConversationId,
            expectedContextVersion: 7,
            [FirstDraftId, SecondDraftId],
            ConversationId,
            currentContextVersion: 7,
            [FirstDraftId, SecondDraftId]);

        Assert.True(result);
    }

    [Fact]
    public void IsCurrent_WhenSelectionMovesToAnotherConversation_ReturnsFalse()
    {
        var result = ClientAttachmentComposerContextPolicy.IsCurrent(
            ConversationId,
            expectedContextVersion: 7,
            [FirstDraftId],
            Guid.NewGuid(),
            currentContextVersion: 8,
            [FirstDraftId]);

        Assert.False(result);
    }

    [Fact]
    public void IsCurrent_WhenSelectionMovesAwayThenBack_ReturnsFalseByVersion()
    {
        var result = ClientAttachmentComposerContextPolicy.IsCurrent(
            ConversationId,
            expectedContextVersion: 7,
            [FirstDraftId],
            ConversationId,
            currentContextVersion: 9,
            [FirstDraftId]);

        Assert.False(result);
    }

    [Theory]
    [InlineData(false, 3, 3, true)]
    [InlineData(true, 4, 3, true)]
    [InlineData(true, 3, 3, false)]
    public void CanApplyProgress_WhenSubmissionOrContextIsStale_ReturnsFalse(
        bool submissionRunning,
        long activeSubmissionVersion,
        long reportedSubmissionVersion,
        bool contextCurrent)
    {
        var result = ClientAttachmentComposerContextPolicy.CanApplyProgress(
            submissionRunning,
            activeSubmissionVersion,
            reportedSubmissionVersion,
            contextCurrent);

        Assert.False(result);
    }

    [Fact]
    public void ShouldClearCommittedDraft_WhenPendingWasNotCommitted_RetainsDraft()
    {
        Assert.False(ClientAttachmentComposerContextPolicy.ShouldClearCommittedDraft(
            pendingCommitted: false,
            contextCurrent: true));
    }

    [Fact]
    public void ShouldClearCommittedDraft_WhenDraftSequenceChanged_RetainsNewDraft()
    {
        var contextCurrent = ClientAttachmentComposerContextPolicy.IsCurrent(
            ConversationId,
            expectedContextVersion: 7,
            [FirstDraftId],
            ConversationId,
            currentContextVersion: 7,
            [SecondDraftId]);

        Assert.False(ClientAttachmentComposerContextPolicy.ShouldClearCommittedDraft(
            pendingCommitted: true,
            contextCurrent));
    }

    [Fact]
    public void ShouldClearCommittedDraft_WhenPendingCommittedAndContextMatches_ClearsDraft()
    {
        Assert.True(ClientAttachmentComposerContextPolicy.ShouldClearCommittedDraft(
            pendingCommitted: true,
            contextCurrent: true));
    }
}
