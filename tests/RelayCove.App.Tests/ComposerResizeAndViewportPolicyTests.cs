using RelayCove.App.Platforms.Windows.Behaviors;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class ComposerResizeAndViewportPolicyTests
{
    [Theory]
    [InlineData(128d, 200d, 120d, 208d)]
    [InlineData(128d, 100d, 400d, 128d)]
    [InlineData(300d, 400d, 0d, 300d)]
    public void CalculateHeight_WhenPointerMoves_ClampsToComposerBounds(
        double startHeight,
        double startY,
        double currentY,
        double expected)
    {
        Assert.Equal(expected, ComposerResizeBehavior.CalculateHeight(startHeight, startY, currentY));
    }

    [Theory]
    [InlineData(95d, true)]
    [InlineData(96d, true)]
    [InlineData(97d, false)]
    public void IsNearBottom_WhenNativeDistanceIsAvailable_UsesNinetySixDipBoundary(double bottomDistanceDip, bool expected)
    {
        Assert.Equal(expected, MessageViewportPolicy.IsNearBottom(bottomDistanceDip, lastVisibleItemIndex: 99, itemCount: 100));
    }

    [Fact]
    public void IsNearBottom_WhenNativeDistanceIsUnavailable_UsesVisibleItemFallback()
    {
        Assert.True(MessageViewportPolicy.IsNearBottom(null, lastVisibleItemIndex: 97, itemCount: 100));
        Assert.False(MessageViewportPolicy.IsNearBottom(null, lastVisibleItemIndex: 96, itemCount: 100));
    }

    [Theory]
    [InlineData(999.9d, 500d, false)]
    [InlineData(1000d, 500d, false)]
    [InlineData(1000.1d, 500d, true)]
    [InlineData(1000.1d, 0d, false)]
    public void ShouldShowJumpToLatest_WhenBottomDistanceExceedsTwoViewports_UsesStrictBoundary(
        double bottomDistanceDip,
        double viewportHeightDip,
        bool expected)
    {
        Assert.Equal(
            expected,
            MessageViewportPolicy.ShouldShowJumpToLatest(bottomDistanceDip, viewportHeightDip));
    }

    [Theory]
    [InlineData(true, 1000d, 900d, true)]
    [InlineData(true, 1000d, 998d, false)]
    [InlineData(false, 1000d, 900d, false)]
    public void ShouldMaintainLatest_WhenLayoutMovesPinnedViewport_OnlyCorrectsMeaningfulBottomGap(
        bool isBottomPinned,
        double scrollableHeight,
        double verticalOffset,
        bool expected)
    {
        Assert.Equal(
            expected,
            MessageViewportPolicy.ShouldMaintainLatest(isBottomPinned, scrollableHeight, verticalOffset));
    }

    [Theory]
    [InlineData(MessageScrollReason.RealtimeFollow, true)]
    [InlineData(MessageScrollReason.ConversationActivated, false)]
    [InlineData(MessageScrollReason.ConversationReactivated, false)]
    [InlineData(MessageScrollReason.ManualJumpToLatest, false)]
    [InlineData(MessageScrollReason.MessageAnchor, false)]
    public void ShouldAnimateLatestScroll_WhenReasonVaries_AnimatesOnlyRealtimeFollow(
        MessageScrollReason reason,
        bool expected)
    {
        Assert.Equal(expected, MessageViewportPolicy.ShouldAnimateLatestScroll(reason));
    }

    [Theory]
    [InlineData(MessageScrollReason.RealtimeFollow, false, true)]
    [InlineData(MessageScrollReason.RealtimeFollow, true, false)]
    [InlineData(MessageScrollReason.ConversationActivated, true, true)]
    [InlineData(MessageScrollReason.ManualJumpToLatest, true, true)]
    public void ShouldIssueLatestScroll_WhenAnimationIsInFlight_DeduplicatesOnlyRealtimeFollow(
        MessageScrollReason reason,
        bool animatedScrollIssued,
        bool expected)
    {
        Assert.Equal(
            expected,
            MessageViewportPolicy.ShouldIssueLatestScroll(reason, animatedScrollIssued));
    }

    [Theory]
    [InlineData(MessageScrollReason.RealtimeFollow, true)]
    [InlineData(MessageScrollReason.ConversationActivated, true)]
    [InlineData(MessageScrollReason.ConversationReactivated, true)]
    [InlineData(MessageScrollReason.ManualJumpToLatest, false)]
    [InlineData(MessageScrollReason.MessageAnchor, false)]
    public void ShouldUseNativeOffsetBeforeTargetRealized_WhenReasonVaries_UsesItForBottomFollowingRequests(
        MessageScrollReason reason,
        bool expected)
    {
        Assert.Equal(expected, MessageViewportPolicy.ShouldUseNativeOffsetBeforeTargetRealized(reason));
    }
}
