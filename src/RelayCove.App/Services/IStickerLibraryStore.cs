namespace RelayCove.App.Services;

public interface IStickerLibraryStore
{
    Task<IReadOnlyList<StickerFavorite>> ListAsync(string accountId, CancellationToken cancellationToken = default);
    Task<StickerFavorite> AddAsync(string accountId, Stream content, string label, CancellationToken cancellationToken = default);
    Task<StickerMedia> ReadAsync(string accountId, string hash, CancellationToken cancellationToken = default);
    Task RenameAsync(string accountId, string hash, string label, CancellationToken cancellationToken = default);
    Task RemoveAsync(string accountId, string hash, CancellationToken cancellationToken = default);
}
