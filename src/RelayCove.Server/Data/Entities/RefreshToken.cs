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

        var normalizedCreatedAt = SqliteValueConverters.NormalizeUtc(createdAt, nameof(createdAt));
        var normalizedExpiresAt = SqliteValueConverters.NormalizeUtc(expiresAt, nameof(expiresAt));
        if (normalizedExpiresAt <= normalizedCreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Refresh tokens must expire after creation.");
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        DeviceName = deviceName;
        CreatedAt = normalizedCreatedAt;
        ExpiresAt = normalizedExpiresAt;
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
        var normalizedRevokedAt = SqliteValueConverters.NormalizeUtc(revokedAt, nameof(revokedAt));
        if (normalizedRevokedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedAt), "Revocation cannot precede token creation.");
        }

        RevokedAt = normalizedRevokedAt;
    }
}
