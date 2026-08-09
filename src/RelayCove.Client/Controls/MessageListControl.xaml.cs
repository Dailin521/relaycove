using System.Windows;
using System.Windows.Automation;
using RelayCove.Client.Accounts;
using WpfControls = System.Windows.Controls;
using WpfPrimitives = System.Windows.Controls.Primitives;

namespace RelayCove.Client.Controls;

public partial class MessageListControl : WpfControls.UserControl
{
    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InteractionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ClientControlInteractionRequestedEventArgs>),
        typeof(MessageListControl));

    public MessageListControl()
    {
        InitializeComponent();
        MessageList.ItemContainerGenerator.ItemsChanged += (_, _) =>
            Dispatcher.BeginInvoke(ApplyOpenAttachmentAutomationNames, System.Windows.Threading.DispatcherPriority.Loaded, MessageList);
    }

    public event EventHandler<ClientControlInteractionRequestedEventArgs> InteractionRequested
    {
        add => AddHandler(InteractionRequestedEvent, value);
        remove => RemoveHandler(InteractionRequestedEvent, value);
    }

    internal WpfControls.ListBox List => MessageList;
    internal WpfControls.TextBlock EmptyText => MessageEmptyText;
    internal WpfControls.Button NewMessageButton => NewMessageIndicatorButton;
    internal WpfControls.ProgressBar LoadingBar => MessageLoadingBar;

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyOpenAttachmentAutomationNames(MessageList);
    }

    private void OnScrollChanged(object sender, WpfControls.ScrollChangedEventArgs e) =>
        Raise("ScrollChanged", sender, e);

    private void OnCardLoaded(object sender, RoutedEventArgs e) =>
        Raise("CardLoaded", sender, e);

    private void OnCardUnloaded(object sender, RoutedEventArgs e) =>
        Raise("CardUnloaded", sender, e);

    private void OnCardDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Raise("CardDataContextChanged", sender, e);

    private void OnReplyClicked(object sender, RoutedEventArgs e) =>
        Raise("ReplyRequested", sender, e);

    private void OnCopyClicked(object sender, RoutedEventArgs e) =>
        Raise("CopyRequested", sender, e);

    private void OnRetryClicked(object sender, RoutedEventArgs e) =>
        Raise("RetryRequested", sender, e);

    private void OnNewMessagesClicked(object sender, RoutedEventArgs e) =>
        Raise("NewMessagesRequested", sender, e);

    private void OnAttachmentOpenClicked(object sender, RoutedEventArgs e) =>
        Raise("AttachmentOpenRequested", sender, e);

    private void OnAttachmentDownloadClicked(object sender, RoutedEventArgs e) =>
        Raise("AttachmentDownloadRequested", sender, e);

    private void OnThumbnailLoaded(object sender, RoutedEventArgs e) =>
        Raise("ThumbnailLoaded", sender, e);

    private void OnThumbnailUnloaded(object sender, RoutedEventArgs e) =>
        Raise("ThumbnailUnloaded", sender, e);

    private void OnThumbnailDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Raise("ThumbnailDataContextChanged", sender, e);

    private void OnImageViewClicked(object sender, RoutedEventArgs e) =>
        Raise("ImageViewRequested", sender, e);

    private void OnReplyReferenceClicked(object sender, RoutedEventArgs e) =>
        Raise("ReplyReferenceClicked", sender, e);

    private void OnLinkClicked(object sender, RoutedEventArgs e) =>
        Raise("LinkClicked", sender, e);

    private void Raise(string interaction, object sender, object originalEventArgs) =>
        RaiseEvent(new ClientControlInteractionRequestedEventArgs(
            InteractionRequestedEvent,
            this,
            interaction,
            sender,
            originalEventArgs));

    private static void ApplyOpenAttachmentAutomationNames(DependencyObject root)
    {
        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is WpfControls.Button { Content: "打开", DataContext: ClientMessageAttachmentPresentation attachment })
            {
                AutomationProperties.SetName(child, $"打开附件：{attachment.DisplayName}");
            }

            ApplyOpenAttachmentAutomationNames(child);
        }
    }
}
