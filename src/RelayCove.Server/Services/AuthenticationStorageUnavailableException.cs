namespace RelayCove.Server.Services;

public sealed class AuthenticationStorageUnavailableException : Exception
{
    public AuthenticationStorageUnavailableException(Exception innerException)
        : base("Authentication storage is temporarily unavailable.", innerException)
    {
    }
}
