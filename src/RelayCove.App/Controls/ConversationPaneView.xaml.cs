using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ConversationPaneView : ContentView
{
    private Microsoft.UI.Xaml.Controls.TextBox? _nativeFilterTextBox;

    public ConversationPaneView()
    {
        InitializeComponent();
        ConversationFilterEntry.HandlerChanged += OnConversationFilterHandlerChanged;
    }

    private void OnConversationFilterHandlerChanged(object? sender, EventArgs eventArgs)
    {
        if (_nativeFilterTextBox is not null)
        {
            _nativeFilterTextBox.KeyDown -= OnConversationFilterKeyDown;
        }
        _nativeFilterTextBox = ConversationFilterEntry.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.TextBox;
        if (_nativeFilterTextBox is not null) _nativeFilterTextBox.KeyDown += OnConversationFilterKeyDown;
    }

    private void OnConversationFilterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs eventArgs)
    {
        if (BindingContext is not ShellViewModel viewModel) return;
        if (eventArgs.Key == Windows.System.VirtualKey.Down)
        {
            viewModel.SelectFirstFilteredConversation();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Windows.System.VirtualKey.Escape)
        {
            viewModel.ClearConversationFilter();
            eventArgs.Handled = true;
        }
    }

    private void OnChannelTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is ChannelItem channel)
        {
            viewModel.ActivateChannel(channel);
        }
    }

    private void OnTopicTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is TopicItem topic)
        {
            viewModel.ActivateTopic(topic);
        }
    }

    private void OnDirectMessageTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is NavigationItem directMessage)
        {
            viewModel.ActivateDirectMessage(directMessage);
        }
    }

    public void FocusBrowseChannelsButton() => BrowseChannelsButton.Focus();
}
