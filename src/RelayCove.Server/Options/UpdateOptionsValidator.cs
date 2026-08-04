using Microsoft.Extensions.Options;

namespace RelayCove.Server.Options;

public sealed class UpdateOptionsValidator : IValidateOptions<UpdateOptions>
{
    public ValidateOptionsResult Validate(string? name, UpdateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            return ValidateOptionsResult.Fail("Update:ManifestPath is required.");
        }

        if (options.ManifestPath.IndexOf('\0') >= 0)
        {
            return ValidateOptionsResult.Fail("Update:ManifestPath is invalid.");
        }

        try
        {
            _ = Path.GetFullPath(options.ManifestPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValidateOptionsResult.Fail("Update:ManifestPath is invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}
