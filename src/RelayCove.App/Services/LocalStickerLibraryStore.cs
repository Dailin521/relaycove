using System.Text.Json;

namespace RelayCove.App.Services;

public sealed class LocalStickerLibraryStore : IStickerLibraryStore
{
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LocalStickerLibraryStore(string root) => this.root = Path.GetFullPath(root);

    public async Task<IReadOnlyList<StickerFavorite>> ListAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadAsync(AccountDirectory(accountId), cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<StickerFavorite> AddAsync(string accountId, Stream content, string label,
        CancellationToken cancellationToken = default)
    {
        var directory = AccountDirectory(accountId);
        var media = StickerImageContent.Validate(await StickerImageContent.ReadBoundedAsync(content,
            StickerImageContent.MaximumBytes, cancellationToken).ConfigureAwait(false));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var favorites = await LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            var existing = favorites.FirstOrDefault(item => item.Hash == media.Hash);
            // Copy immutable original bytes, independent of the imported file or download cache.
            var path = Path.Combine(directory, media.FileName);
            if (!File.Exists(path)) await StickerImageContent.WriteAtomicAsync(path, media.Content, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;
            var favorite = new StickerFavorite(media.Hash, NormalizeLabel(label), media.FileName, media.ContentType, DateTimeOffset.UtcNow);
            favorites.Add(favorite);
            await SaveAsync(directory, favorites, cancellationToken).ConfigureAwait(false);
            return favorite;
        }
        finally { gate.Release(); }
    }

    public async Task<StickerMedia> ReadAsync(string accountId, string hash, CancellationToken cancellationToken = default)
    {
        var directory = AccountDirectory(accountId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var favorite = (await LoadAsync(directory, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Hash == hash) ?? throw new InvalidOperationException("表情已不存在。");
            await using var stream = File.OpenRead(Path.Combine(directory, favorite.FileName));
            var media = StickerImageContent.Validate(await StickerImageContent.ReadBoundedAsync(stream,
                StickerImageContent.MaximumBytes, cancellationToken).ConfigureAwait(false));
            if (media.Hash != favorite.Hash) throw new InvalidDataException("本地表情文件已损坏。");
            return media;
        }
        finally { gate.Release(); }
    }

    public async Task RenameAsync(string accountId, string hash, string label, CancellationToken cancellationToken = default)
    {
        var directory = AccountDirectory(accountId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var favorites = await LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            var index = favorites.FindIndex(item => item.Hash == hash);
            if (index < 0) throw new InvalidOperationException("表情已不存在。");
            favorites[index] = favorites[index] with { Label = NormalizeLabel(label) };
            await SaveAsync(directory, favorites, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string accountId, string hash, CancellationToken cancellationToken = default)
    {
        var directory = AccountDirectory(accountId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var favorites = await LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            var favorite = favorites.FirstOrDefault(item => item.Hash == hash);
            if (favorite is null) return;
            favorites.Remove(favorite);
            await SaveAsync(directory, favorites, cancellationToken).ConfigureAwait(false);
            File.Delete(Path.Combine(directory, favorite.FileName));
        }
        finally { gate.Release(); }
    }

    private string AccountDirectory(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return Path.Combine(root, "favorites", StickerImageContent.HashText(accountId));
    }

    private static string NormalizeLabel(string label)
    {
        var result = new string((label ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return result.Length == 0 ? "表情" : result[..Math.Min(result.Length, 100)];
    }

    private static async Task<List<StickerFavorite>> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "library.json");
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        var bytes = await StickerImageContent.ReadBoundedAsync(stream, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var favorites = JsonSerializer.Deserialize<List<StickerFavorite>>(bytes) ?? throw new InvalidDataException("本地表情目录已损坏。");
        foreach (var favorite in favorites)
        {
            if (favorite is null || favorite.Hash is null || !StickerImageContent.IsHash(favorite.Hash)
                || favorite.FileName is null || !new[] { ".png", ".jpg", ".gif", ".webp" }.Any(extension =>
                    favorite.FileName == favorite.Hash + extension))
                throw new InvalidDataException("本地表情目录已损坏。");
        }
        return favorites;
    }

    private static Task SaveAsync(string directory, List<StickerFavorite> favorites, CancellationToken cancellationToken) =>
        StickerImageContent.WriteAtomicAsync(Path.Combine(directory, "library.json"), JsonSerializer.SerializeToUtf8Bytes(favorites), cancellationToken);
}
