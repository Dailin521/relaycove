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
}
