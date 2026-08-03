using RelayCove.Client.Storage;

namespace RelayCove.Client.Tests.Storage;

public sealed class ClientTextMessageContentValidatorTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData(" leading and trailing ")]
    [InlineData("first\r\nsecond\tvalue")]
    [InlineData("😀")]
    public void IsValid_WhenTextMatchesServerContract_ReturnsTrue(string content)
    {
        Assert.True(ClientTextMessageContentValidator.IsValid(content));
    }

    [Fact]
    public void IsValid_WhenTextHasMaximumUnicodeScalars_ReturnsTrue()
    {
        Assert.True(ClientTextMessageContentValidator.IsValid(
            string.Concat(Enumerable.Repeat("😀", 4_000))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("value\0")]
    [InlineData("value\u001B")]
    public void IsValid_WhenTextViolatesServerContract_ReturnsFalse(string? content)
    {
        Assert.False(ClientTextMessageContentValidator.IsValid(content));
    }

    [Fact]
    public void IsValid_WhenTextContainsUnpairedSurrogate_ReturnsFalse()
    {
        Assert.False(ClientTextMessageContentValidator.IsValid(new string('\uD800', 1)));
    }

    [Fact]
    public void IsValid_WhenTextExceedsMaximumUnicodeScalars_ReturnsFalse()
    {
        Assert.False(ClientTextMessageContentValidator.IsValid(
            new string('a', ClientTextMessageContentValidator.MaximumScalarCount + 1)));
    }
}
