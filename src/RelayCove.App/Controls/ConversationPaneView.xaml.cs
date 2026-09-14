using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ConversationPaneView : ContentView
{
    public ConversationPaneView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        OnPropertyChanged(nameof(ViewModel));
    }

    private void OnConversationTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel && eventArgs.Parameter is ConversationListItem conversation)
            viewModel.ActivateConversation(conversation);
    }

    private void OnNewConversationClicked(object? sender, EventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            NewConversationButton.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement anchor) return;

        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "发起私聊",
            Command = viewModel.OpenNewConversationCommand
        });
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = "发起群聊",
            Command = viewModel.ShowNewChannelConversationCommand,
            IsEnabled = viewModel.CanCreatePrivateGroup
        });
        menu.ShowAt(anchor);
    }

    private static void OnConversationPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: ConversationListItem conversation })
            conversation.IsPointerOver = true;
    }

    private static void OnConversationPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: ConversationListItem conversation })
            conversation.IsPointerOver = false;
    }

    public void FocusConversationFilter() => ConversationFilterEntry.Focus();

    public void FocusChannelMenuButton() => NewConversationButton.Focus();

    public void FocusTopicMenuButton() => NewConversationButton.Focus();
}
