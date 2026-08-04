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
            var fullPath = Path.GetFullPath(options.ManifestPath);
            var rootPath = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(Path.GetDirectoryName(options.ManifestPath)) ||
                options.ManifestPath.EndsWith(Path.DirectorySeparatorChar) ||
                options.ManifestPath.EndsWith(Path.AltDirectorySeparatorChar) ||
                Directory.Exists(fullPath))
            {
                return ValidateOptionsResult.Fail("Update:ManifestPath must identify a file under a parent directory.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValidateOptionsResult.Fail("Update:ManifestPath is invalid.");
        }

        return ValidateOptionsResult.Success;
    }
}
