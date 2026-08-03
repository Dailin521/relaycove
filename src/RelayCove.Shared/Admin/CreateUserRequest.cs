namespace RelayCove.Shared.Admin;

public sealed record CreateUserRequest(
    string UserName,
    string DisplayName,
    string Password,
    bool IsAdmin)
{
    public override string ToString()
    {
        return $"{nameof(CreateUserRequest)} {{ UserName = {UserName}, DisplayName = {DisplayName}, Password = [REDACTED], IsAdmin = {IsAdmin} }}";
    }
}
