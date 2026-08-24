using System.Security.Cryptography;
using System.Text;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class NotificationAvatarFileStore : INotificationAvatarFileStore
{
    private const long MaximumAvatarBytes = 1024 * 1024;
    private readonly IClientSession _session;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public NotificationAvatarFileStore(IClientSession session)
        : this(session, FileSystem.CacheDirectory)
    {
    }

    internal NotificationAvatarFileStore(IClientSession session, string cacheRoot)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
            ? throw new ArgumentException("A cache root is required.", nameof(cacheRoot))
            : Path.GetFullPath(cacheRoot);
    }

    public async Task<Uri?> GetAvatarUriAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || _session.AccountId is not { } accountId) return null;
        var cacheDirectory = GetAccountCacheDirectory(_cacheRoot, accountId);
        var fileStem = CreateFileStem(accountId, sourceUrl);
        foreach (var extension in new[] { ".png", ".jpg" })
        {
            var cachedPath = Path.Combine(cacheDirectory, fileStem + extension);
            if (File.Exists(cachedPath)) return new Uri(cachedPath);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var cachedPath = Path.Combine(cacheDirectory, fileStem + extension);
                if (File.Exists(cachedPath)) return new Uri(cachedPath);
            }

            var result = await _session.GetRealmMediaAsync(
                new RealmMediaRequest(sourceUrl, RealmMediaKind.Avatar, MaximumAvatarBytes),
                cancellationToken).ConfigureAwait(false);
            var fileExtension = GetSafeImageExtension(result.ContentType);
            if (fileExtension is null || result.Content.Length == 0 || result.Content.LongLength > MaximumAvatarBytes)
            {
                return null;
            }

            Directory.CreateDirectory(cacheDirectory);
            var destinationPath = Path.Combine(cacheDirectory, fileStem + fileExtension);
            var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, result.Content, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return new Uri(destinationPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal static string CreateFileStem(AccountId accountId, string sourceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{accountId.Value}\n{sourceUrl}"))).ToLowerInvariant();
    }

    public async Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cacheDirectory = GetAccountCacheDirectory(_cacheRoot, accountId);
            if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true);

            // Earlier Stage 27 candidates used one shared flat directory.
            // These files cannot be attributed without reversing their hash,
            // so a requested local-cache cleanup removes the obsolete cache.
            var legacyDirectory = Path.Combine(_cacheRoot, "notification-avatars");
            if (Directory.Exists(legacyDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(legacyDirectory)) File.Delete(path);
                if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any()) Directory.Delete(legacyDirectory);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Avatar files are disposable cache. A locked shell file must not
            // turn a successful logout into a false failure.
        }
        finally
        {
            _writeGate.Release();
        }
    }

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
            _ => null
        };
    }
}
