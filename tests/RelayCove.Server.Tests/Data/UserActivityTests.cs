using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Data;

public sealed class UserActivityTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RecordLogin_WhenTimestampHasSubMillisecondPrecision_NormalizesAndAdvancesAllFields()
    {
        var user = CreateUser();

        user.RecordLogin(CreatedAt.AddMinutes(5).AddTicks(9876));

        var expected = CreatedAt.AddMinutes(5);
        Assert.Equal(expected, user.LastLoginAt);
        Assert.Equal(expected, user.LastOnlineAt);
        Assert.Equal(expected, user.UpdatedAt);
    }

    [Fact]
    public void RecordActivity_WhenClockMovesBackward_DoesNotRegressTimestamps()
    {
        var user = CreateUser();
        user.RecordActivity(CreatedAt.AddMinutes(10));

        user.RecordActivity(CreatedAt.AddMinutes(5));

        Assert.Equal(CreatedAt.AddMinutes(10), user.LastOnlineAt);
        Assert.Equal(CreatedAt.AddMinutes(10), user.UpdatedAt);
    }

    [Fact]
    public void SetPasswordHash_WhenTimestampIsProvided_AdvancesUpdatedAtWithoutLeakingHash()
    {
        var user = CreateUser();

        user.SetPasswordHash("new-password-hash", CreatedAt.AddMinutes(1).AddTicks(1));

        Assert.Equal("new-password-hash", user.PasswordHash);
        Assert.Equal(CreatedAt.AddMinutes(1), user.UpdatedAt);
    }

    private static User CreateUser() => new(
        Guid.Parse("2aabed19-12de-48c7-a173-4c8938111bcc"),
        "alice",
        "Alice",
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        CreatedAt,
        new UserNameNormalizer());
}
