namespace RelayCove.Client.Auth;

internal static class ClientAuthenticationUri
{
    public static Uri CanonicalizeServerBaseUri(Uri serverBaseUri)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        var isHttpScheme = serverBaseUri.IsAbsoluteUri &&
            (string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(serverBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        if (!isHttpScheme ||
            string.IsNullOrEmpty(serverBaseUri.Host) ||
            !string.IsNullOrEmpty(serverBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(serverBaseUri.Query) ||
            !string.IsNullOrEmpty(serverBaseUri.Fragment))
        {
            throw new ArgumentException(
                "Server base URI must be an absolute HTTP(S) URI without user info, query, or fragment.",
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
}
