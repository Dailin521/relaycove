namespace RelayCove.Core;

public sealed record GetRealmMediaRequest(CredentialEnvelope Credentials, RealmMediaRequest Media)
{
    public override string ToString() => "GetRealmMediaRequest { Credentials = [redacted], Media = [redacted] }";
}
