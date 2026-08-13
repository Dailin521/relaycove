namespace RelayCove.App.ViewModels;

public sealed record ContactItem(long UserId, string Name, string? AvatarUrl = null, bool IsBot = false)
{
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool ShowFallback => !HasAvatar;
    public string Initial => string.IsNullOrWhiteSpace(Name)
        ? "?"
        : IsBot
            ? "BOT"
            : Name.Trim()[0].ToString().ToUpperInvariant();
}
