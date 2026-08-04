using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class SearchQueryValidatorTests
{
    private readonly SearchQueryValidator validator = new();

    [Theory]
    [InlineData("中文关键词")]
    [InlineData("emoji 😀")]
    [InlineData("literal%_\\")]
    [InlineData("\u200Bformat")]
    public void Validate_WhenTrimmedKeywordAndLimitAreValid_ReturnsNoErrors(string keyword)
    {
        Assert.Empty(validator.Validate(keyword, limit: null));
        Assert.Empty(validator.Validate(keyword, SearchQueryValidator.MaximumLimit));
    }

    [Fact]
    public void Validate_WhenKeywordHasOuterWhitespace_ValidatesNormalizedValue()
    {
        const string keyword = "  中文 😀  ";

        Assert.Empty(validator.Validate(keyword, limit: null));
        Assert.Equal("中文 😀", SearchQueryValidator.NormalizeKeyword(keyword));
        Assert.True(SearchQueryValidator.IsValidNormalizedKeyword("中文 😀"));
        Assert.False(SearchQueryValidator.IsValidNormalizedKeyword(keyword));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u2003\u2009")]
    [InlineData("line\nfeed")]
    [InlineData("bad\u0001control")]
    public void Validate_WhenKeywordIsInvalid_ReturnsOnlyKeywordError(string? keyword)
    {
        var errors = validator.Validate(keyword, limit: null);

        Assert.Equal(["keyword"], errors.Keys);
    }

    [Fact]
    public void Validate_WhenKeywordContainsMalformedUtf16_ReturnsOnlyKeywordError()
    {
        var errors = validator.Validate("\uD800", limit: null);

        Assert.Equal(["keyword"], errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WhenLimitIsInvalid_ReturnsOnlyLimitError(int limit)
    {
        var errors = validator.Validate("中文", limit);

        Assert.Equal(["limit"], errors.Keys);
    }

    [Fact]
    public void IsValidNormalizedKeyword_WhenKeywordHas64UnicodeScalars_AcceptsExactBoundary()
    {
        var valid = string.Concat(Enumerable.Repeat("😀", SearchQueryValidator.MaximumKeywordLength));
        var invalid = valid + "😀";

        Assert.True(SearchQueryValidator.IsValidNormalizedKeyword(valid));
        Assert.False(SearchQueryValidator.IsValidNormalizedKeyword(invalid));
    }
}
