namespace RelayCove.Core;

public sealed class AuthenticationRequest
{
    public AuthenticationRequest(RealmEndpoint realm, string email, string password)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Realm = realm;
        Email = email.Trim();
        Password = password;
    }

    public RealmEndpoint Realm { get; }
    public string Email { get; }
    public string Password { get; }

    public override string ToString() =>
        "AuthenticationRequest { Realm = [redacted], Email = [redacted], Password = [redacted] }";
}
