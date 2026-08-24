using RelayCove.App.Platforms.Windows.Behaviors;

namespace RelayCove.App.Tests;

public sealed class HorizontalDragScrollBehaviorTests
{
    [Theory]
    [InlineData(10, 12, false)]
    [InlineData(10, 14, true)]
    [InlineData(10, 6, true)]
    public void ShouldStartDrag_WhenPointerMoves_UsesMouseThreshold(
        double startX,
        double currentX,
        bool expected) =>
        Assert.Equal(expected, HorizontalDragScrollBehavior.ShouldStartDrag(startX, currentX));

    [Theory]
    [InlineData(30, 100, 80, 200, 50)]
    [InlineData(10, 100, 140, 200, 0)]
    [InlineData(180, 100, 20, 200, 200)]
    public void CalculateOffset_WhenDragged_ClampsAndTracksInversePointerMovement(
        double startOffset,
        double startX,
        double currentX,
        double scrollableWidth,
        double expected) =>
        Assert.Equal(expected, HorizontalDragScrollBehavior.CalculateOffset(
            startOffset,
            startX,
            currentX,
            scrollableWidth));
}
