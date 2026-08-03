namespace RelayCove.Client.Auth;

internal enum ClientCredentialReadStatus
{
    Loaded = 1,
    NotFound = 2,
    Corrupt = 3,
    Unavailable = 4,
}
