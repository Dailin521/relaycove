using RelayCove.Server.Services;

namespace RelayCove.Server.Data.Entities;

public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    public RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        string deviceName,
        DateTime createdAt,
        DateTime expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Refresh token IDs cannot be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User IDs cannot be empty.", nameof(userId));
        }

        if (!RefreshTokenHasher.IsValidHash(tokenHash))
        {
            throw new ArgumentException("Refresh token hashes must be 43-character Base64Url values.", nameof(tokenHash));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (deviceName.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceName), "Device names cannot exceed 128 characters.");
        }

        RequireUtc(createdAt, nameof(createdAt));
        RequireUtc(expiresAt, nameof(expiresAt));
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Refresh tokens must expire after creation.");
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        DeviceName = deviceName;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public string DeviceName { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public User User { get; private set; } = null!;

    public void Revoke(DateTime revokedAt)
    {
        RequireUtc(revokedAt, nameof(revokedAt));
        if (revokedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedAt), "Revocation cannot precede token creation.");
        }

        RevokedAt = revokedAt;
    }

    private static void RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Persistent timestamps must use DateTimeKind.Utc.", parameterName);
        }
    }
}
