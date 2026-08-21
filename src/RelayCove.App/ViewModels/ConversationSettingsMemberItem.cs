namespace RelayCove.App.ViewModels;

public sealed record ConversationSettingsMemberItem(
    long UserId,
    string Name,
    string? AvatarUrl = null,
    bool IsBot = false,
    bool IsOwner = false)
{
    public string Initial => AvatarInitials.Create(Name, IsBot);
    public string RoleLabel => IsOwner ? "群主" : string.Empty;
}
