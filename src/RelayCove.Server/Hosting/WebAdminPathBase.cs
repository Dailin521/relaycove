using Microsoft.AspNetCore.Http;

namespace RelayCove.Server.Hosting;

internal static class WebAdminPathBase
{
    public static PathString Parse(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return PathString.Empty;
        }

        var value = configuredValue.Trim();
        if (!value.StartsWith("/", StringComparison.Ordinal) ||
            value.EndsWith("/", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RelayCove:PathBase must be empty or an absolute path without a trailing slash.");
        }

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.Contains('\\'))
            {
                throw new InvalidOperationException("RelayCove:PathBase cannot contain dot segments or backslashes.");
            }
        }

        return new PathString(value);
    }
}
