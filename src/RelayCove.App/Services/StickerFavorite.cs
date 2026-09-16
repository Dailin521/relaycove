namespace RelayCove.App.Services;

public sealed record StickerFavorite(string Hash, string Label, string FileName, string ContentType, DateTimeOffset AddedAt);
