using RelayCove.App.Platforms.Windows.Behaviors;

namespace RelayCove.App.Tests;

public sealed class MessageLinkBehaviorTests
{
    [Fact]
    public void ShouldUpdateHover_WhenDragEnds_KeepsDecorationsStableUntilSelectionIsCleared()
    {
        const string selectedUrl = "https://example.test/page";

        Assert.False(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: true, selectedText: string.Empty));
        Assert.False(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: true, selectedText: selectedUrl));
        // Releasing the button must not enable the destructive decoration update.
        Assert.False(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: false, selectedText: selectedUrl));
        Assert.True(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: false, selectedText: string.Empty));
    }

    [Theory]
    [InlineData("https://example.test/page")]
    [InlineData("example")]
    [InlineData("正文 https://example.test/page\n下一行")]
    [InlineData(" ")]
    public void ShouldUpdateHover_WhenTextRemainsSelected_DoesNotChangeDecorationsOnPointerMoveOrExit(string selectedText)
    {
        Assert.False(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: false, selectedText));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldUpdateHover_WhenNoDragOrSelection_AllowsNormalLinkHover(string? selectedText)
    {
        Assert.True(MessageLinkBehavior.ShouldUpdateHover(isLeftButtonPressed: false, selectedText));
    }
}
