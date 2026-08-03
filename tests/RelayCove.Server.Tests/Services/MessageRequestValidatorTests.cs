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

    [Theory]
    [InlineData(0, null, null, "messageId")]
    [InlineData(-1, null, null, "messageId")]
    [InlineData(1, -1, null, "before")]
    [InlineData(1, 101, null, "before")]
    [InlineData(1, null, -1, "after")]
    [InlineData(1, null, 101, "after")]
    public void ValidateAround_WhenValueIsOutOfRange_ReturnsCamelCaseFieldError(
        long messageId,
        int? before,
        int? after,
        string expectedField)
    {
        var errors = validator.ValidateAround(messageId, before, after);

        Assert.Equal([expectedField], errors.Keys);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 100, 100)]
    public void ValidateAround_WhenValuesAreWithinRange_ReturnsNoErrors(
        long messageId,
        int? before,
        int? after)
    {
        Assert.Empty(validator.ValidateAround(messageId, before, after));
    }
}
