using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class UserNameNormalizerTests
{
    private readonly UserNameNormalizer normalizer = new();

    [Theory]
    [InlineData("lq", "LQ")]
    [InlineData("Alice", "ALICE")]
    [InlineData("team.member-01", "TEAM.MEMBER-01")]
    [InlineData("ABC_def", "ABC_DEF")]
    public void Normalize_WhenUserNameIsValid_ReturnsStableLookupKey(string userName, string expected)
    {
        var actual = normalizer.Normalize(userName);

        Assert.Equal(expected, actual);
        Assert.Equal(actual, normalizer.Normalize(actual));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("---")]
    [InlineData("alice smith")]
    [InlineData("管理员")]
    [InlineData("ali\u200Bce")]
    [InlineData("alice\u202E")]
    public void TryNormalize_WhenUserNameIsInvalid_ReturnsFalse(string? userName)
    {
        var succeeded = normalizer.TryNormalize(userName, out var normalizedUserName);

        Assert.False(succeeded);
        Assert.Empty(normalizedUserName);
    }

    [Fact]
    public void SetUserName_WhenCaseChanges_UpdatesRawAndNormalizedValuesTogether()
    {
        var user = CreateUser("Alice");

        user.SetUserName("aLiCe-2", normalizer);

        Assert.Equal("aLiCe-2", user.UserName);
        Assert.Equal("ALICE-2", user.NormalizedUserName);
        Assert.True(typeof(User).GetProperty(nameof(User.UserName))!.GetSetMethod(nonPublic: true)!.IsPrivate);
        Assert.True(typeof(User).GetProperty(nameof(User.NormalizedUserName))!.GetSetMethod(nonPublic: true)!.IsPrivate);
    }

    private User CreateUser(string userName) => new(
        Guid.Parse("5c5583d9-38c7-44b3-a836-a38f3a280b8d"),
        userName,
        "Alice",
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        new DateTime(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc),
        normalizer);
}
