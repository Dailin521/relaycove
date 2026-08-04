using Microsoft.Extensions.Options;

namespace RelayCove.Server.Options;

public sealed class UploadOptionsValidator : IValidateOptions<UploadOptions>
{
    public ValidateOptionsResult Validate(string? name, UploadOptions options)
    {
        var failures = new List<string>();
        if (options.MaximumFileBytes is < 1 or > UploadOptions.AbsoluteMaximumFileBytes)
        {
            failures.Add(
                $"Uploads:MaximumFileBytes must be between 1 and {UploadOptions.AbsoluteMaximumFileBytes}.");
        }

        if (options.PermitLimit is < 1 or > 1_000)
        {
            failures.Add("Uploads:PermitLimit must be between 1 and 1000.");
        }

        if (options.RateLimitWindowSeconds is < 1 or > 86_400)
        {
            failures.Add("Uploads:RateLimitWindowSeconds must be between 1 and 86400.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
