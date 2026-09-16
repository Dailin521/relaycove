namespace RelayCove.App.Services;

public sealed record StickerCatalogEntry(string Id, string Label, string SourceUrl, string ThumbnailUrl,
    int Width, int Height, bool Animated, long Bytes, string Category, string CategoryTitle);
