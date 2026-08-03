using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class SyncRequestValidatorTests
{
    private readonly SyncRequestValidator validator = new();

    [Theory]
    [InlineData(null, null, null, "cursor")]
    [InlineData(-1L, null, null, "cursor")]
    [InlineData(1L, 0L, null, "snapshotUpperBound")]
    [InlineData(0L, -1L, null, "snapshotUpperBound")]
    [InlineData(0L, null, 0, "limit")]
    [InlineData(0L, null, 201, "limit")]
    public void Validate_WhenInputIsInvalid_ReturnsCamelCaseFieldError(
        long? cursor,
        long? snapshotUpperBound,
        int? limit,
        string expectedField)
    {
        var errors = validator.Validate(cursor, snapshotUpperBound, limit);

        Assert.Equal([expectedField], errors.Keys);
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(0L, 0L, 1)]
    [InlineData(10L, 10L, 200)]
    public void Validate_WhenInputIsValid_ReturnsNoErrors(
        long? cursor,
        long? snapshotUpperBound,
        int? limit)
    {
        Assert.Empty(validator.Validate(cursor, snapshotUpperBound, limit));
    }

    [Fact]
    public void Limits_AreStable()
    {
        Assert.Equal(100, SyncRequestValidator.DefaultLimit);
        Assert.Equal(200, SyncRequestValidator.MaximumLimit);
    }
}
