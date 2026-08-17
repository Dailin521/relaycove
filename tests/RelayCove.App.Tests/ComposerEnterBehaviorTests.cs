using RelayCove.App.Platforms.Windows.Behaviors;
using Windows.System;

namespace RelayCove.App.Tests;

public sealed class ComposerEnterBehaviorTests
{
    [Theory]
    [InlineData(VirtualKey.Enter, false, false, true)]
    [InlineData(VirtualKey.Enter, true, false, false)]
    [InlineData(VirtualKey.Enter, false, true, false)]
    [InlineData(VirtualKey.Space, false, false, false)]
    public void ShouldSend_WhenKeyStateChanges_ReturnsExpectedDecision(
        VirtualKey key,
        bool isControlPressed,
        bool isTextCompositionActive,
        bool expected)
    {
        Assert.Equal(expected, ComposerEnterBehavior.ShouldSend(key, isControlPressed, isTextCompositionActive));
    }
}
