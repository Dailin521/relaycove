namespace RelayCove.Shared.Users;

public sealed record UserDirectoryEntryDto(
    Guid UserId,
    string UserName,
    string DisplayName)
{
    public override string ToString() =>
        $"{nameof(UserDirectoryEntryDto)} {{ UserId = [REDACTED], " +
        "UserName = [REDACTED], DisplayName = [REDACTED] }";
}
