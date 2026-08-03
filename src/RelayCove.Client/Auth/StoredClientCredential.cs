namespace RelayCove.Client.Auth;

internal sealed class StoredClientCredential
{
    internal StoredClientCredential(
        Uri serverBaseUri,
        Guid userId,
        string refreshToken)
    {
        ServerBaseUri = serverBaseUri;
        UserId = userId;
        RefreshToken = refreshToken;
    }

    public Uri ServerBaseUri { get; }

    public Guid UserId { get; }

    public string RefreshToken { get; }

    public override string ToString() =>
        $"{nameof(StoredClientCredential)} {{ ServerBaseUri = [REDACTED], " +
        "UserId = [REDACTED], RefreshToken = [REDACTED] }";
}
