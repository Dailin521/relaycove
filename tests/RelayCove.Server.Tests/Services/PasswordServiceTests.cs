using Microsoft.AspNetCore.Identity;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class PasswordServiceTests
{
    private const string Password = "correct-horse-battery-staple";
    private readonly User user = CreateUser();

    [Fact]
    public void HashPassword_WhenCalledTwice_UsesRandomSaltAndVerifiesBothHashes()
    {
        var service = CreateService(100_000);

        var first = service.HashPassword(user, Password);
        var second = service.HashPassword(user, Password);

        Assert.NotEqual(first, second);
        Assert.Equal(PasswordVerificationOutcome.Success, service.VerifyPassword(user, first, Password));
        Assert.Equal(PasswordVerificationOutcome.Success, service.VerifyPassword(user, second, Password));
        Assert.Equal(PasswordVerificationOutcome.Failed, service.VerifyPassword(user, first, "wrong-password"));
    }

    [Fact]
    public void VerifyPassword_WhenHashUsesLowerIterationCount_ReturnsSuccessRehashNeeded()
    {
        var oldService = CreateService(10_000);
        var currentService = CreateService(100_000);
        var oldHash = oldService.HashPassword(user, Password);

        var outcome = currentService.VerifyPassword(user, oldHash, Password);

        Assert.Equal(PasswordVerificationOutcome.SuccessRehashNeeded, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQAAAAIAAYagAAAAEA")]
    public void VerifyPassword_WhenHashIsMalformed_ReturnsFailed(string? passwordHash)
    {
        var service = CreateService(100_000);

        var outcome = service.VerifyPassword(user, passwordHash, Password);

        Assert.Equal(PasswordVerificationOutcome.Failed, outcome);
    }

    [Fact]
    public void VerifyDummyPassword_WhenPasswordIsNullOrArbitrary_DoesNotThrow()
    {
        var service = CreateService(100_000);

        service.VerifyDummyPassword(null);
        service.VerifyDummyPassword("arbitrary-password");
    }

    private static PasswordService CreateService(int iterationCount)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount,
        });

        return new PasswordService(new PasswordHasher<User>(options));
    }

    private static User CreateUser() => new(
        Guid.Parse("2aabed19-12de-48c7-a173-4c8938111bcc"),
        "alice",
        "Alice",
        "pending-password-hash",
        isAdmin: false,
        isDisabled: false,
        new DateTime(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc),
        new UserNameNormalizer());
}
