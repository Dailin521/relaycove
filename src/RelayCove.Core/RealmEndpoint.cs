using System.Globalization;

namespace RelayCove.Core;

public sealed record RealmEndpoint
{
    private RealmEndpoint(Uri uri)
    {
        Uri = uri;
    }

    public Uri Uri { get; }

    public string AbsoluteUri => Uri.AbsoluteUri;

    public static RealmEndpoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!System.Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            (parsed.AbsolutePath.Length > 0 && parsed.AbsolutePath != "/"))
        {
            throw new ArgumentException("A realm endpoint must be an absolute HTTPS origin.", nameof(value));
        }

        var builder = new UriBuilder(Uri.UriSchemeHttps, parsed.IdnHost.ToLowerInvariant(), parsed.IsDefaultPort ? -1 : parsed.Port)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return new RealmEndpoint(builder.Uri);
    }

    public static bool TryParse(string? value, out RealmEndpoint? endpoint)
    {
        try
        {
            endpoint = value is null ? null : Parse(value);
            return endpoint is not null;
        }
        catch (ArgumentException)
        {
            endpoint = null;
            return false;
        }
    }

    public override string ToString() => AbsoluteUri;
}
