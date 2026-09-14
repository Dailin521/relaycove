using System.Security.Cryptography;
using System.Text;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class NotificationAvatarFileStore : INotificationAvatarFileStore
{
    private readonly AvatarCache _cache;

    public NotificationAvatarFileStore(AvatarCache cache) => _cache = cache;

    public event EventHandler<AvatarChangedEventArgs>? AvatarChanged
    {
        add => _cache.AvatarChanged += value;
        remove => _cache.AvatarChanged -= value;
    }

    public async Task<Uri?> GetAvatarUriAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        try { return (await _cache.GetAsync(sourceUrl, cancellationToken).ConfigureAwait(false)).FileUri; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static string CreateFileStem(AccountId accountId, string sourceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{accountId.Value}\n{sourceUrl}"))).ToLowerInvariant();
    }

    public Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        _cache.ClearAccountAsync(accountId, cancellationToken);

    internal static string GetAccountCacheDirectory(string cacheRoot, AccountId accountId)
    {
        var accountHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(accountId.Value))).ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(cacheRoot), "notification-avatars", accountHash);
    }

    internal static string? GetSafeImageExtension(string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim();
        return normalized.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };
    }
}
