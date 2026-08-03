using Microsoft.Extensions.Options;
using RelayCove.Server.Services;

namespace RelayCove.Server.Options;

public sealed class BootstrapAdminOptionsValidator(NewUserValidator newUserValidator)
    : IValidateOptions<BootstrapAdminOptions>
{
    public ValidateOptionsResult Validate(string? name, BootstrapAdminOptions options)
    {
        if (!options.Enabled)
        {
            return HasCredentialValues(options)
                ? ValidateOptionsResult.Fail(
                    "BootstrapAdmin credentials must be removed when BootstrapAdmin:Enabled is false.")
                : ValidateOptionsResult.Success;
        }

        var errors = newUserValidator.Validate(options.UserName, options.DisplayName, options.Password);
        if (errors.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = errors.SelectMany(pair => pair.Value.Select(message =>
            $"BootstrapAdmin:{ToConfigurationName(pair.Key)} {message}"));
        return ValidateOptionsResult.Fail(failures);
    }

    private static bool HasCredentialValues(BootstrapAdminOptions options) =>
        !string.IsNullOrEmpty(options.UserName) ||
        !string.IsNullOrEmpty(options.DisplayName) ||
        !string.IsNullOrEmpty(options.Password);

    private static string ToConfigurationName(string fieldName) => fieldName switch
    {
        "userName" => "UserName",
        "displayName" => "DisplayName",
        "password" => "Password",
        _ => fieldName,
    };
}
