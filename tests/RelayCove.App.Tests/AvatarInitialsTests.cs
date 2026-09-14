using RelayCove.App.ViewModels;

namespace RelayCove.App.Tests;

public sealed class AvatarInitialsTests
{
    [Theory]
    [InlineData("Maya Chen", "M")]
    [InlineData("Alex Wu", "A")]
    [InlineData("林远", "林")]
    [InlineData("  Sarah Li  ", "S")]
    [InlineData("👩🏽‍💻 Developer", "👩🏽‍💻")]
    [InlineData("e\u0301lodie", "E\u0301")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Create_WhenDisplayNameProvided_ReturnsSingleTextElement(string? displayName, string expected)
    {
        Assert.Equal(expected, AvatarInitials.Create(displayName));
    }

    [Fact]
    public void Create_WhenBot_ReturnsBotMarker()
    {
        Assert.Equal("BOT", AvatarInitials.Create("Build Bot", isBot: true));
    }
}
