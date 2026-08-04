using System.Net;

namespace RelayCove.Client.Updates;

internal static class ClientUpdateServerUri
{
    public static Uri Canonicalize(Uri serverBaseUri)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        var isHttps = serverBaseUri.IsAbsoluteUri && string.Equals(
            serverBaseUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = serverBaseUri.IsAbsoluteUri && string.Equals(
                serverBaseUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase) &&
            IsLoopbackHost(serverBaseUri);
        if (!serverBaseUri.IsAbsoluteUri ||
            (!isHttps && !isLoopbackHttp) ||
            string.IsNullOrEmpty(serverBaseUri.Host) ||
            !string.IsNullOrEmpty(serverBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(serverBaseUri.Query) ||
            !string.IsNullOrEmpty(serverBaseUri.Fragment))
        {
            throw new ArgumentException(
                "The update server base URI must use HTTPS, except for explicit HTTP loopback addresses, and must not contain user info, query, or fragment.",
                nameof(serverBaseUri));
        }

        var builder = new UriBuilder(serverBaseUri)
        {
            Scheme = serverBaseUri.Scheme.ToLowerInvariant(),
            Host = serverBaseUri.IdnHost.ToLowerInvariant(),
        };
        if (serverBaseUri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += '/';
        }

        return builder.Uri;
    }

    private static bool IsLoopbackHost(Uri serverBaseUri)
    {
        if (string.Equals(serverBaseUri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var host = serverBaseUri.Host.Trim('[', ']');
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
