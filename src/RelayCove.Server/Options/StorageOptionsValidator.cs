using Microsoft.Extensions.Options;

namespace RelayCove.Server.Options;

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.UploadsPath))
        {
            return ValidateOptionsResult.Fail("Storage:UploadsPath is required.");
        }

        if (options.UploadsPath.IndexOf('\0') >= 0)
        {
            return ValidateOptionsResult.Fail("Storage:UploadsPath is invalid.");
        }

        try
        {
            _ = Path.GetFullPath(options.UploadsPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValidateOptionsResult.Fail("Storage:UploadsPath is invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}
