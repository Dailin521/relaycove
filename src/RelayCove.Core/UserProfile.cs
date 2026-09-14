namespace RelayCove.Core;

public sealed record UserProfile
{
    public UserProfile(
        long userId,
        string fullName,
        string? email = null,
        bool isActive = true,
        string? avatarUrl = null,
        int? avatarVersion = null,
        bool isBot = false,
        UserAvatarSource avatarSource = UserAvatarSource.Unknown)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        UserId = userId;
        FullName = fullName;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        IsActive = isActive;
        AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl;
        AvatarVersion = avatarVersion;
        IsBot = isBot;
        AvatarSource = avatarSource;
    }

    public long UserId { get; init; }
    public string FullName { get; init; }
    public string? Email { get; init; }
    public bool IsActive { get; init; }
    public string? AvatarUrl { get; init; }
    public int? AvatarVersion { get; init; }
    public bool IsBot { get; init; }
    public UserAvatarSource AvatarSource { get; init; }
    public string? DisplayAvatarUrl => AvatarSource == UserAvatarSource.Generated ? null : AvatarUrl;

    public UserProfile PreserveAvatarSource(UserProfile? previous) =>
        AvatarSource == UserAvatarSource.Unknown && previous is not null &&
        UserId == previous.UserId && AvatarUrl == previous.AvatarUrl && AvatarVersion == previous.AvatarVersion
            ? this with { AvatarSource = previous.AvatarSource }
            : this;
}
