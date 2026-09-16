namespace RelayCove.App.Services;

public sealed record StickerCatalogSnapshot(IReadOnlyList<StickerCatalogEntry> Entries, bool IsOffline);
