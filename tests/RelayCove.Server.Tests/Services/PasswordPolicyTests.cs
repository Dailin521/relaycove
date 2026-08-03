using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Services;

public sealed class PasswordPolicyTests
{
    private readonly PasswordPolicy policy = new();

    [Theory]
    [InlineData("a secure phrase with spaces")]
    [InlineData("这是一条足够长且安全的中文密码短语")]
    public void Validate_WhenPasswordIsLongAndNotContextual_ReturnsNoErrors(string password)
    {
        var errors = policy.Validate(password, "alice", "Alice Chen");

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenPasswordUsesSupplementaryUnicode_CountsScalarsInsteadOfUtf16Units()
    {
        var password = string.Concat(Enumerable.Repeat("🙂", PasswordPolicy.MinimumLength));

        var errors = policy.Validate(password, "alice", "Alice");

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenPasswordIsOutsideScalarBounds_ReturnsLengthError()
    {
        var tooShort = new string('x', PasswordPolicy.MinimumLength - 1);
        var tooLong = new string('x', PasswordPolicy.MaximumLength + 1);

        Assert.Contains(policy.Validate(tooShort, "alice", "Alice"), error => error.Contains("between", StringComparison.Ordinal));
        Assert.Contains(policy.Validate(tooLong, "alice", "Alice"), error => error.Contains("between", StringComparison.Ordinal));
        Assert.Empty(policy.Validate(new string('x', PasswordPolicy.MaximumLength), "alice", "Alice"));
    }

    [Fact]
    public void Validate_WhenPasswordContainsControlCharacter_ReturnsControlError()
    {
        var errors = policy.Validate("a sufficiently long\npassword", "alice", "Alice");

        Assert.Contains(errors, error => error.Contains("control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhenPasswordContainsUnpairedSurrogate_ReturnsUnicodeError()
    {
        var password = new string('x', PasswordPolicy.MinimumLength) + '\uD800';

        var errors = policy.Validate(password, "alice", "Alice");

        Assert.Contains(errors, error => error.Contains("Unicode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("correct horse battery staple", "alice", "Alice")]
    [InlineData("administrator-123", "administrator", "Administrator")]
    [InlineData("alicealicealice", "alice", "Alice")]
    [InlineData("RelayCove-RelayCove", "alice", "Alice")]
    public void Validate_WhenPasswordIsCommonOrContextual_ReturnsWeakPasswordError(
        string password,
        string userName,
        string displayName)
    {
        var errors = policy.Validate(password, userName, displayName);

        Assert.Contains(errors, error => error.Contains("common", StringComparison.OrdinalIgnoreCase));
    }
}
