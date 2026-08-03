using RelayCove.Server.Options;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Options;

public sealed class BootstrapAdminOptionsValidatorTests
{
    private const string ValidPassword = "a secure bootstrap phrase";
    private readonly BootstrapAdminOptionsValidator validator = new(
        new NewUserValidator(new UserNameNormalizer(), new PasswordPolicy()));

    [Fact]
    public void Validate_WhenDisabledAndEmpty_ReturnsSuccess()
    {
        var result = validator.Validate(null, new BootstrapAdminOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenDisabledButCredentialsRemain_FailsWithoutEchoingPassword()
    {
        var options = CreateValidOptions();
        options.Enabled = false;

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain(ValidPassword, string.Join(' ', result.Failures!), StringComparison.Ordinal);
        Assert.DoesNotContain(ValidPassword, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenEnabledAndValid_ReturnsSuccess()
    {
        var result = validator.Validate(null, CreateValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenEnabledButFieldsAreInvalid_FailsWithoutEchoingPassword()
    {
        const string invalidPassword = "short";
        var options = new BootstrapAdminOptions
        {
            Enabled = true,
            UserName = "管理员",
            DisplayName = "",
            Password = invalidPassword,
        };

        var result = validator.Validate(null, options);
        var failures = string.Join(' ', result.Failures!);

        Assert.True(result.Failed);
        Assert.Contains("BootstrapAdmin:UserName", failures, StringComparison.Ordinal);
        Assert.Contains("BootstrapAdmin:DisplayName", failures, StringComparison.Ordinal);
        Assert.Contains("BootstrapAdmin:Password", failures, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidPassword, failures, StringComparison.Ordinal);
    }

    private static BootstrapAdminOptions CreateValidOptions() => new()
    {
        Enabled = true,
        UserName = "bootstrap-admin",
        DisplayName = "Bootstrap Administrator",
        Password = ValidPassword,
    };
}
