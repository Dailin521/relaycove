using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed partial class ConversationMessagePresentation : ObservableObject
{
    private readonly ResettableObservableCollection<MessageItem> _messages = [];

    internal ConversationMessagePresentation(string conversationKey, ShellViewModel viewModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ConversationKey = conversationKey;
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public string ConversationKey { get; }
    public ShellViewModel ViewModel { get; }
    public ObservableCollection<MessageItem> Messages => _messages;

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    internal ResettableObservableCollection<MessageItem> MutableMessages => _messages;
}
