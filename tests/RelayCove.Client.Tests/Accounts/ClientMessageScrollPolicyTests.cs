using RelayCove.Client.Accounts;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientMessageScrollPolicyTests
{
    [Fact]
    public void Decide_WhenConversationOpens_ScrollsToLatestAndObservesIt()
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: false,
            previousOldestMessageId: null,
            previousLatestMessageId: null,
            nextOldestMessageId: 1,
            nextLatestMessageId: 50,
            wasNearBottom: false,
            targetMessageId: null,
            targetChanged: false);

        Assert.Null(decision.ScrollToMessageId);
        Assert.True(decision.ScrollToEnd);
        Assert.Equal(50, decision.ObservedThroughMessageId);
        Assert.False(decision.PreservePrependOffset);
    }

    [Fact]
    public void Decide_WhenNotificationTargetChanges_ScrollsOnlyThroughTarget()
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 40,
            previousLatestMessageId: 50,
            nextOldestMessageId: 10,
            nextLatestMessageId: 60,
            wasNearBottom: true,
            targetMessageId: 25,
            targetChanged: true);

        Assert.Equal(25, decision.ScrollToMessageId);
        Assert.Equal(25, decision.ObservedThroughMessageId);
        Assert.False(decision.PreservePrependOffset);
    }

    [Fact]
    public void Decide_WhenOlderMessagesPrepend_PreservesOffsetAndDoesNotAdvanceRead()
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 1,
            nextLatestMessageId: 100,
            wasNearBottom: false,
            targetMessageId: null,
            targetChanged: false);

        Assert.True(decision.PreservePrependOffset);
        Assert.Null(decision.ScrollToMessageId);
        Assert.Null(decision.ObservedThroughMessageId);
    }

    [Fact]
    public void Decide_WhenSameWindowIsRepublished_DoesNotTreatLayoutGrowthAsPrepend()
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 51,
            nextLatestMessageId: 100,
            wasNearBottom: false,
            targetMessageId: null,
            targetChanged: false);

        Assert.False(decision.PreservePrependOffset);
        Assert.Null(decision.ScrollToMessageId);
        Assert.False(decision.ShowNewMessageIndicator);
        Assert.Null(decision.ObservedThroughMessageId);
    }

    [Theory]
    [InlineData(true, null, false, 101L)]
    [InlineData(false, null, true, null)]
    public void Decide_WhenRealtimeAppends_OnlyFollowsUserAtLatest(
        bool wasNearBottom,
        long? expectedScrollTarget,
        bool expectedIndicator,
        long? expectedObservedThrough)
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 51,
            nextLatestMessageId: 101,
            wasNearBottom,
            targetMessageId: null,
            targetChanged: false);

        Assert.Equal(expectedScrollTarget, decision.ScrollToMessageId);
        Assert.Equal(expectedIndicator, decision.ShowNewMessageIndicator);
        Assert.Equal(expectedObservedThrough, decision.ObservedThroughMessageId);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Decide_WhenPendingRowAppends_FollowsOnlyUserAtLatest(
        bool wasNearBottom,
        bool expectedScrollToEnd,
        bool expectedIndicator)
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 51,
            nextLatestMessageId: 100,
            wasNearBottom,
            targetMessageId: null,
            targetChanged: false,
            contentAppended: true,
            hasNextItems: true);

        Assert.Equal(expectedScrollToEnd, decision.ScrollToEnd);
        Assert.Equal(expectedIndicator, decision.ShowNewMessageIndicator);
        Assert.Equal(wasNearBottom ? 100 : null, decision.ObservedThroughMessageId);
    }

    [Fact]
    public void Decide_WhenLocalPendingMessageIsConfirmed_DoesNotScrollOrFlashViewport()
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 51,
            nextLatestMessageId: 101,
            wasNearBottom: true,
            targetMessageId: null,
            targetChanged: false,
            contentAppended: false,
            hasNextItems: true,
            replacesExistingLocalItem: true);

        Assert.False(decision.ScrollToEnd);
        Assert.Null(decision.ScrollToMessageId);
        Assert.False(decision.ShowNewMessageIndicator);
    }

    [Theory]
    [InlineData(true, true, false, 102L)]
    [InlineData(false, false, true, null)]
    public void Decide_WhenPendingConfirmationAndNewMessageArriveTogether_PreservesRealAppendBehavior(
        bool wasNearBottom,
        bool expectedScrollToEnd,
        bool expectedIndicator,
        long? expectedObservedThrough)
    {
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation: true,
            previousOldestMessageId: 51,
            previousLatestMessageId: 100,
            nextOldestMessageId: 51,
            nextLatestMessageId: 102,
            wasNearBottom,
            targetMessageId: null,
            targetChanged: false,
            contentAppended: true,
            hasNextItems: true,
            replacesExistingLocalItem: true);

        Assert.Equal(expectedScrollToEnd, decision.ScrollToEnd);
        Assert.Equal(expectedIndicator, decision.ShowNewMessageIndicator);
        Assert.Equal(expectedObservedThrough, decision.ObservedThroughMessageId);
    }
}
