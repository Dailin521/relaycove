using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class MentionCandidateQueryValidatorTests
{
    private readonly MentionCandidateQueryValidator validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Alice_1")]
    [InlineData("team.member-01")]
    public void Validate_WhenQueryAndLimitAreValid_ReturnsNoErrors(string query)
    {
        Assert.Empty(validator.Validate(query, limit: null));
        Assert.Empty(validator.Validate(query, MentionCandidateQueryValidator.MaximumLimit));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("alice smith")]
    [InlineData("用户")]
    [InlineData("ali%ce")]
    [InlineData("ali\\ce")]
    [InlineData("ali\u200Bce")]
    public void Validate_WhenQueryIsInvalid_ReturnsOnlyQueryError(string? query)
    {
        var errors = validator.Validate(query, limit: null);

        Assert.Equal(["query"], errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WhenLimitIsInvalid_ReturnsOnlyLimitError(int limit)
    {
        var errors = validator.Validate("alice", limit);

        Assert.Equal(["limit"], errors.Keys);
    }

    [Fact]
    public void Validate_WhenQueryIsTooLong_ReturnsQueryError()
    {
        var errors = validator.Validate(
            new string('a', MentionCandidateQueryValidator.MaximumQueryLength + 1),
            limit: null);

        Assert.Equal(["query"], errors.Keys);
    }
}
