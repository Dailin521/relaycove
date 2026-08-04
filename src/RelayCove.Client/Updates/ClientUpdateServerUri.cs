namespace RelayCove.Client.Updates;

internal static class ClientUpdateServerUri
{
    public static Uri Canonicalize(Uri serverBaseUri)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        if (!serverBaseUri.IsAbsoluteUri ||
            serverBaseUri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(serverBaseUri.Host) ||
            !string.IsNullOrEmpty(serverBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(serverBaseUri.Query) ||
            !string.IsNullOrEmpty(serverBaseUri.Fragment))
        {
            throw new ArgumentException(
                "The update server base URI must be an absolute HTTP(S) URI without user info, query, or fragment.",
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
