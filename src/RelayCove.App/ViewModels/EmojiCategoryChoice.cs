using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed partial class EmojiCategoryChoice(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
