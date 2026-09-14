using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed partial class ConversationContactChoice : ObservableObject
{
    public ConversationContactChoice(long userId, string name, string? avatarUrl, bool isBot, bool isSelf = false)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UserId = userId;
        Name = name;
        AvatarUrl = avatarUrl;
        IsBot = isBot;
        IsSelf = isSelf;
    }

    public long UserId { get; }
    public string Name { get; }
    [ObservableProperty]
    public partial string? AvatarUrl { get; set; }
    public bool IsBot { get; }
    public bool IsSelf { get; }
    public string KindLabel => IsSelf ? "自己" : IsBot ? "机器人" : "联系人";
    public string Initial => IsBot ? "BOT" : Name.Trim()[0].ToString().ToUpperInvariant();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
