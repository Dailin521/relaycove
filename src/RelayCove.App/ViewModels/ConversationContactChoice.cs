using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed partial class ConversationContactChoice : ObservableObject
{
    public ConversationContactChoice(long userId, string name, string? avatarUrl, bool isBot)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UserId = userId;
        Name = name;
        AvatarUrl = avatarUrl;
        IsBot = isBot;
    }

    public long UserId { get; }
    public string Name { get; }
    public string? AvatarUrl { get; }
    public bool IsBot { get; }
    public string KindLabel => IsBot ? "机器人" : "联系人";
    public string Initial => IsBot ? "BOT" : Name.Trim()[0].ToString().ToUpperInvariant();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
