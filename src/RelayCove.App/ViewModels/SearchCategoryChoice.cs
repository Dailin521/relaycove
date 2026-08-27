using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class SearchCategoryChoice(MessageSearchFilter filter, string label) : ObservableObject
{
    public MessageSearchFilter Filter { get; } = filter;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
