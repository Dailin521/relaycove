using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

/// <summary>A locally selectable channel color; it does not persist until the user saves.</summary>
public sealed partial class ChannelColorOption(string hex) : ObservableObject
{
    public string Hex { get; } = hex;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}
