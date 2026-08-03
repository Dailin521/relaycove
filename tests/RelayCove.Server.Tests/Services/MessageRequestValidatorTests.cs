using RelayCove.Server.Services;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Services;

public sealed class MessageRequestValidatorTests
{
    private readonly MessageRequestValidator validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRead_WhenMessageIdIsNotPositive_ReturnsCamelCaseFieldError(long messageId)
    {
        var errors = validator.ValidateRead(new MarkConversationReadRequest(messageId));

        Assert.Equal(["messageId"], errors.Keys);
    }

    [Fact]
    public void ValidateRead_WhenRequestIsNull_ReturnsMessageIdError()
    {
        var errors = validator.ValidateRead(null);

        Assert.Equal(["messageId"], errors.Keys);
    }

    [Fact]
    public void ValidateRead_WhenMessageIdIsPositive_ReturnsNoErrors()
    {
        Assert.Empty(validator.ValidateRead(new MarkConversationReadRequest(1)));
    }
}
