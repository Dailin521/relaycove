using RelayCove.App.Services;

namespace RelayCove.App.ViewModels;

public sealed record StickerPickerItem(StickerCatalogEntry? Catalog, StickerFavorite? Favorite, string? AccountKey = null)
{
    public string Label => Favorite?.Label ?? Catalog?.Label ?? "表情";
    public string ActionLabel => Favorite is null ? "♡" : "⋯";
    public string ActionDescription => Favorite is null ? "收藏表情" : "管理收藏";
}
