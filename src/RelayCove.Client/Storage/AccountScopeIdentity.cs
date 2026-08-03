using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RelayCove.Client.Storage;

public sealed record AccountScopeIdentity
{
    private const string DatabaseFileName = "relaycove.db";

    private AccountScopeIdentity(
        string id,
        Uri canonicalServerBaseUri,
        Guid userId,
        string rootDirectory,
        string scopeDirectory,
        string databasePath)
    {
        Id = id;
        CanonicalServerBaseUri = canonicalServerBaseUri;
        UserId = userId;
        RootDirectory = rootDirectory;
        ScopeDirectory = scopeDirectory;
        DatabasePath = databasePath;
    }

    public string Id { get; }

    public Uri CanonicalServerBaseUri { get; }

    public Guid UserId { get; }

    public string RootDirectory { get; }

    public string ScopeDirectory { get; }

    public string DatabasePath { get; }

    public override string ToString() =>
        $"{nameof(AccountScopeIdentity)} {{ Id = {Id}, " +
        "CanonicalServerBaseUri = [REDACTED], UserId = [REDACTED], " +
        "RootDirectory = [REDACTED], ScopeDirectory = [REDACTED], DatabasePath = [REDACTED] }";

    public static AccountScopeIdentity Create(
        Uri serverBaseUri,
        Guid userId,
        string rootDirectory)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("Root directory must be an absolute path.", nameof(rootDirectory));
        }

        var canonicalServerBaseUri = CanonicalizeServerBaseUri(serverBaseUri);
        var hashInput = canonicalServerBaseUri.AbsoluteUri +
            "\n" +
            userId.ToString("D").ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var id = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var scopeDirectory = Path.GetFullPath(Path.Combine(normalizedRoot, id));
        EnsureChildPath(normalizedRoot, scopeDirectory);
        var databasePath = Path.GetFullPath(Path.Combine(scopeDirectory, DatabaseFileName));
        EnsureChildPath(scopeDirectory, databasePath);
        return new AccountScopeIdentity(
            id,
            canonicalServerBaseUri,
            userId,
            normalizedRoot,
            scopeDirectory,
            databasePath);
    }

    private static Uri CanonicalizeServerBaseUri(Uri serverBaseUri)
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

    private static void EnsureChildPath(string parentPath, string childPath)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(parentPath),
            Path.GetFullPath(childPath));
        if (Path.IsPathFullyQualified(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved account scope path escaped its root directory.");
        }
    }
}
