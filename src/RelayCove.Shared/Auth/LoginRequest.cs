namespace RelayCove.Shared.Auth;

public sealed record LoginRequest(
    string UserName,
    string Password,
    string DeviceName,
    string ClientVersion)
{
    public override string ToString()
    {
        return $"{nameof(LoginRequest)} {{ UserName = {UserName}, Password = [REDACTED], DeviceName = {DeviceName}, ClientVersion = {ClientVersion} }}";
    }
}
