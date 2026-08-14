using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class AvatarInitialsTests
{
    [Theory]
    [InlineData("Maya Chen", "MA")]
    [InlineData("Alex Wu", "AL")]
    [InlineData("林远", "林")]
    [InlineData("  Sarah Li  ", "SA")]
    public void Create_WhenDisplayNameProvided_ReturnsWebParityInitials(string displayName, string expected)
    {
        Assert.Equal(expected, AvatarInitials.Create(displayName));
    }

    [Fact]
    public void Create_WhenBot_ReturnsBotMarker()
    {
        Assert.Equal("BOT", AvatarInitials.Create("Build Bot", isBot: true));
    }
}
