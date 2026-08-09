using System.Windows;
using System.Windows.Automation;
using RelayCove.Client.Accounts;
using WpfControls = System.Windows.Controls;
using WpfPrimitives = System.Windows.Controls.Primitives;

namespace RelayCove.Client.Controls;

public partial class MessageListControl : WpfControls.UserControl
{
    private bool aligningShortHistory;

    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InteractionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ClientControlInteractionRequestedEventArgs>),
        typeof(MessageListControl));

    public MessageListControl()
    {
        InitializeComponent();
        MessageList.ItemContainerGenerator.ItemsChanged += (_, _) =>
            Dispatcher.BeginInvoke(ApplyDeferredListPresentation, System.Windows.Threading.DispatcherPriority.Loaded);
        MessageList.SizeChanged += (_, _) => AlignShortHistoryToBottom();
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
        AlignShortHistoryToBottom();
    }

    private void OnScrollChanged(object sender, WpfControls.ScrollChangedEventArgs e) =>
        Raise("ScrollChanged", sender, e);

    private void ApplyDeferredListPresentation()
    {
        ApplyOpenAttachmentAutomationNames(MessageList);
        AlignShortHistoryToBottom();
    }

    // Short conversations have no scroll range, so reserve only the unused
    // viewport above them. Long conversations retain a zero padding, pixel
    // virtualized scroll surface and therefore never move while dragged.
    private void AlignShortHistoryToBottom()
    {
        if (aligningShortHistory || MessageList.Items.Count == 0)
        {
            return;
        }

        var scrollViewer = FindVisualChild<WpfControls.ScrollViewer>(MessageList);
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var contentHeight = Math.Max(0, scrollViewer.ExtentHeight - MessageList.Padding.Top);
        var requiredTopPadding = Math.Max(0, scrollViewer.ViewportHeight - contentHeight);
        if (Math.Abs(requiredTopPadding - MessageList.Padding.Top) < 0.5)
        {
            return;
        }

        aligningShortHistory = true;
        try
        {
            MessageList.Padding = new Thickness(0, requiredTopPadding, 0, 0);
        }
        finally
        {
            aligningShortHistory = false;
        }
    }

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

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
