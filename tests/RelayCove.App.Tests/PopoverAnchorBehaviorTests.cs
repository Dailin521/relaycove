using RelayCove.App.Platforms.Windows.Behaviors;

namespace RelayCove.App.Tests;

public sealed class PopoverAnchorBehaviorTests
{
    [Theory]
    [InlineData(360d, 200d, 360d, 204d)]
    [InlineData(500d, 400d, 500d, 404d)]
    [InlineData(1160d, 770d, 946d, 536d)]
    [InlineData(-10d, -5d, 12d, 12d)]
    [InlineData(1500d, 900d, 974d, 558d)]
    public void CalculatePosition_WhenPointerMoves_FollowsItAndKeepsMenuInsidePage(
        double pointerX, double pointerY, double expectedX, double expectedY)
    {
        var position = PopoverAnchorBehavior.CalculatePosition(pointerX, pointerY, 214d, 230d, 1200d, 800d);

        Assert.Equal(expectedX, position.X);
        Assert.Equal(expectedY, position.Y);
    }

    [Fact]
    public void CalculateRelativeTranslation_WhenReopenedAtAnotherPosition_DoesNotAccumulatePreviousOffset()
    {
        var first = PopoverAnchorBehavior.CalculateRelativeTranslation(800d, 100d, 0d);
        var second = PopoverAnchorBehavior.CalculateRelativeTranslation(300d, 100d + first, first);
        var repeated = PopoverAnchorBehavior.CalculateRelativeTranslation(300d, 100d + second, second);

        Assert.Equal(200d, second);
        Assert.Equal(second, repeated);
    }

    [Theory]
    [InlineData(320d, 60d, 0d, 260d)]
    [InlineData(320d, 320d, 260d, 260d)]
    public void CalculateRelativeTranslation_WhenPopoverHasAParentOffset_AnchorsInPageCoordinates(
        double targetPosition,
        double currentPosition,
        double currentTranslation,
        double expectedTranslation)
    {
        var translation = PopoverAnchorBehavior.CalculateRelativeTranslation(
            targetPosition,
            currentPosition,
            currentTranslation);

        Assert.Equal(expectedTranslation, translation);
    }
}
