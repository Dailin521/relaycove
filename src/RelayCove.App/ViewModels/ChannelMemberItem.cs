using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed partial class ChannelMemberItem(
    long userId,
    string name,
    bool isMember,
    string? email = null,
    bool isActive = true,
    bool isBot = false) : ObservableObject
{
    public long UserId { get; } = userId;
    public string Name { get; } = name;
    public string? Email { get; } = email;
    public bool IsActive { get; } = isActive;
    public bool IsBot { get; } = isBot;
    public bool IsMember { get; } = isMember;
    public bool IsCandidate => !IsMember && IsActive;
    public string EmailLabel => string.IsNullOrWhiteSpace(Email) ? "未提供邮箱地址" : Email;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}
