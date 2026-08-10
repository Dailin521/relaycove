namespace RelayCove.Core;

public sealed class CredentialEnvelope
{
    public CredentialEnvelope(RealmEndpoint realm, string email, long userId, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (!email.Contains('@', StringComparison.Ordinal)) throw new ArgumentException("Email must contain @.", nameof(email));
        Realm = realm;
        Email = email.Trim();
        UserId = userId;
        ApiKey = apiKey;
    }

    public RealmEndpoint Realm { get; }
    public string Email { get; }
    public long UserId { get; }
    public string ApiKey { get; }

    public override string ToString() => "CredentialEnvelope { Realm = [redacted], Email = [redacted], UserId = [redacted], ApiKey = [redacted] }";
}
