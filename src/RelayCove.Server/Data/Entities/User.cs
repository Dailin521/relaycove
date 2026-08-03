using RelayCove.Server.Services;

namespace RelayCove.Server.Data.Entities;

public sealed class User
{
    private User()
    {
    }

    public User(
        Guid id,
        string userName,
        string displayName,
        string passwordHash,
        bool isAdmin,
        bool isDisabled,
        DateTime createdAt,
        UserNameNormalizer userNameNormalizer)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User IDs cannot be empty.", nameof(id));
        }

        Id = id;
        SetUserName(userName, userNameNormalizer);
        SetDisplayName(displayName);
        SetPasswordHash(passwordHash);
        IsAdmin = isAdmin;
        IsDisabled = isDisabled;
        CreatedAt = SqliteValueConverters.NormalizeUtc(createdAt, nameof(createdAt));
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }

    public string UserName { get; private set; } = string.Empty;

    public string NormalizedUserName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public Guid? AvatarAttachmentId { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsAdmin { get; private set; }

    public bool IsDisabled { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? LastLoginAt { get; private set; }

    public DateTime? LastOnlineAt { get; private set; }

    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();

    public ICollection<Conversation> CreatedConversations { get; } = new List<Conversation>();

    public ICollection<ConversationMember> ConversationMemberships { get; } = new List<ConversationMember>();

    public void SetUserName(string userName, UserNameNormalizer userNameNormalizer)
    {
        ArgumentNullException.ThrowIfNull(userNameNormalizer);
        var normalizedUserName = userNameNormalizer.Normalize(userName);

        UserName = userName;
        NormalizedUserName = normalizedUserName;
    }

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void SetPasswordHash(string passwordHash, DateTime updatedAt)
    {
        SetPasswordHash(passwordHash);
        AdvanceUpdatedAt(updatedAt);
    }

    public void RecordLogin(DateTime loggedInAt)
    {
        var effectiveTime = AdvanceUpdatedAt(loggedInAt);
        LastLoginAt = effectiveTime;
        LastOnlineAt = effectiveTime;
    }

    public void RecordActivity(DateTime activityAt)
    {
        LastOnlineAt = AdvanceUpdatedAt(activityAt);
    }

    internal static User CreatePasswordHashSubject() => new();

    private DateTime AdvanceUpdatedAt(DateTime value)
    {
        var normalizedValue = SqliteValueConverters.NormalizeUtc(value, nameof(value));
        if (normalizedValue < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "User activity cannot precede creation.");
        }

        UpdatedAt = normalizedValue < UpdatedAt ? UpdatedAt : normalizedValue;
        return UpdatedAt;
    }

    private void SetDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName), "Display names cannot exceed 100 characters.");
        }

        DisplayName = displayName;
    }

}
