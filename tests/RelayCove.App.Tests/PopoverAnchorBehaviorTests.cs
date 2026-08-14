using RelayCove.App.Platforms.Windows.Behaviors;

namespace RelayCove.App.Tests;

public sealed class PopoverAnchorBehaviorTests
{
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
