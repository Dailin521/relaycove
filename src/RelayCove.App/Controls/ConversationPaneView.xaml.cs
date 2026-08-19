using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ConversationPaneView : ContentView
{
    private Microsoft.UI.Xaml.Controls.TextBox? _nativeFilterTextBox;
    private VisualElement? _channelMenuTrigger;
    private VisualElement? _topicMenuTrigger;

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

    private static void OnChannelPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: ChannelItem channel }) channel.IsPointerOver = true;
    }

    private static void OnChannelPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: ChannelItem channel }) channel.IsPointerOver = false;
    }

    private void OnNewChannelTopicClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not BindableObject { BindingContext: ChannelItem channel } || BindingContext is not ShellViewModel viewModel) return;
        viewModel.OpenNewChannelTopicForChannelCommand.Execute(channel);
    }

    private void OnChannelMenuClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement { BindingContext: ChannelItem channel } trigger || BindingContext is not ShellViewModel viewModel) return;
        _channelMenuTrigger = trigger;
        var request = CreateChannelMenuRequest(trigger, channel) ?? new ChannelMenuRequest(channel, 12d, 68d);
        viewModel.OpenChannelMenuAtCommand.Execute(request);
    }

    private void OnTopicTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is TopicItem topic)
        {
            viewModel.ActivateTopic(topic);
        }
    }

    private static void OnTopicPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: TopicItem topic }) topic.IsPointerOver = true;
    }

    private static void OnTopicPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: TopicItem topic }) topic.IsPointerOver = false;
    }

    private void OnTopicMenuClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement { BindingContext: TopicItem topic } trigger || BindingContext is not ShellViewModel viewModel) return;
        _topicMenuTrigger = trigger;
        var request = CreateTopicMenuRequest(trigger, topic) ?? new TopicMenuRequest(topic, 12d, 112d);
        viewModel.OpenTopicMenuAtCommand.Execute(request);
    }

    private void OnDirectMessageTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is NavigationItem directMessage)
        {
            viewModel.ActivateDirectMessage(directMessage);
        }
    }

    public void FocusBrowseChannelsButton() => BrowseChannelsButton.Focus();

    public void FocusChannelMenuButton() => _channelMenuTrigger?.Focus();

    public void FocusTopicMenuButton() => _topicMenuTrigger?.Focus();

    private static ChannelMenuRequest? CreateChannelMenuRequest(VisualElement trigger, ChannelItem channel)
    {
#if WINDOWS
        var source = trigger.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<Microsoft.UI.Xaml.FrameworkElement>()
            .FirstOrDefault();
        if (source is null || pageRoot is null) return null;
        try
        {
            var point = source.TransformToVisual(pageRoot)
                .TransformPoint(new Windows.Foundation.Point(source.ActualWidth, 0d));
            return new ChannelMenuRequest(channel, point.X, point.Y);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
#else
        return null;
#endif
    }

    private static TopicMenuRequest? CreateTopicMenuRequest(VisualElement trigger, TopicItem topic)
    {
#if WINDOWS
        var source = trigger.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<Microsoft.UI.Xaml.FrameworkElement>()
            .FirstOrDefault();
        if (source is null || pageRoot is null) return null;
        try
        {
            var point = source.TransformToVisual(pageRoot)
                .TransformPoint(new Windows.Foundation.Point(source.ActualWidth, 0d));
            return new TopicMenuRequest(topic, point.X, point.Y);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
#else
        return null;
#endif
    }
}
