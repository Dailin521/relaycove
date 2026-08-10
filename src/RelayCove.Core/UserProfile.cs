namespace RelayCove.Core;

public sealed record UserProfile
{
    public UserProfile(long userId, string fullName, string? email = null, bool isActive = true)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        UserId = userId;
        FullName = fullName;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        IsActive = isActive;
    }

    public long UserId { get; init; }
    public string FullName { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
}
