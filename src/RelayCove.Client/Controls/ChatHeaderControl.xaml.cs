using System.Windows;
using RelayCove.Client.Presentation;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace RelayCove.Client.Controls;

public partial class ChatHeaderControl : UserControl
{
    public static readonly DependencyProperty HeadingProperty = RegisterTextProperty(
        nameof(Heading),
        "请选择会话");

    public static readonly DependencyProperty NoticeProperty = RegisterTextProperty(
        nameof(Notice),
        "选择左侧真实会话以查看消息。");

    public static readonly DependencyProperty MembersSummaryProperty = RegisterTextProperty(
        nameof(MembersSummary),
        "成员：请选择会话");

    public static readonly DependencyProperty IsMembersEnabledProperty =
        DependencyProperty.Register(
            nameof(IsMembersEnabled),
            typeof(bool),
            typeof(ChatHeaderControl),
            new PropertyMetadata(false));

    public static readonly RoutedEvent MembersRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(MembersRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ChatHeaderMembersRequestedEventArgs>),
        typeof(ChatHeaderControl));

    public static readonly RoutedEvent SearchRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SearchRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ChatHeaderSearchRequestedEventArgs>),
        typeof(ChatHeaderControl));

    public static readonly RoutedEvent UnavailableFeatureRequestedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(UnavailableFeatureRequested),
            RoutingStrategy.Bubble,
            typeof(EventHandler<ClientUnavailableFeatureRequestedEventArgs>),
            typeof(ChatHeaderControl));

    public ChatHeaderControl()
    {
        InitializeComponent();
    }

    public string Heading
    {
        get => (string)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string Notice
    {
        get => (string)GetValue(NoticeProperty);
        set => SetValue(NoticeProperty, value);
    }

    public string MembersSummary
    {
        get => (string)GetValue(MembersSummaryProperty);
        set => SetValue(MembersSummaryProperty, value);
    }

    public bool IsMembersEnabled
    {
        get => (bool)GetValue(IsMembersEnabledProperty);
        set => SetValue(IsMembersEnabledProperty, value);
    }

    public event EventHandler<ChatHeaderMembersRequestedEventArgs> MembersRequested
    {
        add => AddHandler(MembersRequestedEvent, value);
        remove => RemoveHandler(MembersRequestedEvent, value);
    }

    public event EventHandler<ChatHeaderSearchRequestedEventArgs> SearchRequested
    {
        add => AddHandler(SearchRequestedEvent, value);
        remove => RemoveHandler(SearchRequestedEvent, value);
    }

    public event EventHandler<ClientUnavailableFeatureRequestedEventArgs>
        UnavailableFeatureRequested
    {
        add => AddHandler(UnavailableFeatureRequestedEvent, value);
        remove => RemoveHandler(UnavailableFeatureRequestedEvent, value);
    }

    internal TextBlock HeadingText => HeadingTextElement;

    internal TextBlock NoticeText => NoticeTextElement;

    internal TextBlock MembersSummaryText => MembersSummaryTextElement;

    internal Button MembersButton => MembersButtonElement;

    internal Button SearchButton => SearchButtonElement;

    internal Button PinButton => PinButtonElement;

    internal Button NotificationsButton => NotificationsButtonElement;

    internal Button MoreButton => MoreButtonElement;

    private static DependencyProperty RegisterTextProperty(string name, string defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(ChatHeaderControl),
            new PropertyMetadata(defaultValue));

    private void OnMembersClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new ChatHeaderMembersRequestedEventArgs(MembersRequestedEvent, this));
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new ChatHeaderSearchRequestedEventArgs(SearchRequestedEvent, this));
    }

    private void OnUnavailableClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && TryGetUnavailableFeature(button, out var featureId, out var displayName))
        {
            RaiseEvent(new ClientUnavailableFeatureRequestedEventArgs(
                UnavailableFeatureRequestedEvent,
                this,
                featureId,
                displayName));
        }

        e.Handled = true;
    }

    private bool TryGetUnavailableFeature(
        Button button,
        out ClientUiFeatureId featureId,
        out string displayName)
    {
        if (ReferenceEquals(button, PinButtonElement))
        {
            featureId = ClientUiFeatureId.ConversationPin;
            displayName = "置顶会话";
            return true;
        }

        if (ReferenceEquals(button, NotificationsButtonElement))
        {
            featureId = ClientUiFeatureId.ConversationNotifications;
            displayName = "会话通知";
            return true;
        }

        if (ReferenceEquals(button, MoreButtonElement))
        {
            featureId = ClientUiFeatureId.ConversationMore;
            displayName = "更多会话操作";
            return true;
        }

        featureId = default;
        displayName = string.Empty;
        return false;
    }
}
