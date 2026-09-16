namespace RelayCove.App.Services;

public interface IStickerCatalogService
{
    Task<StickerCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default);
    Task<StickerMedia> GetMediaAsync(StickerCatalogEntry entry, bool thumbnail = false,
        CancellationToken cancellationToken = default);
}
