using System.Windows;
using WpfControls = System.Windows.Controls;

namespace RelayCove.Client.Controls;

public partial class ConversationPanelControl : WpfControls.UserControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ConversationPanelControl), new PropertyMetadata(new CornerRadius(0)));

    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InteractionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ClientControlInteractionRequestedEventArgs>),
        typeof(ConversationPanelControl));

    public ConversationPanelControl() => InitializeComponent();

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public event EventHandler<ClientControlInteractionRequestedEventArgs> InteractionRequested
    {
        add => AddHandler(InteractionRequestedEvent, value);
        remove => RemoveHandler(InteractionRequestedEvent, value);
    }

    internal WpfControls.TextBox SearchTextBox => ConversationSearchTextBox;
    internal WpfControls.ListBox List => ConversationList;
    internal WpfControls.TextBlock EmptyText => ConversationEmptyText;
    internal WpfControls.TextBlock SidebarDisplayName => SidebarDisplayNameText;
    internal WpfControls.TextBlock SidebarConnection => SidebarConnectionText;
    internal WpfControls.Button AllFilterButton => AllConversationFilterButton;
    internal WpfControls.Button UnreadFilterButton => UnreadConversationFilterButton;
    internal WpfControls.Button ChannelFilterButton => ChannelConversationFilterButton;
    internal WpfControls.Button DirectFilterButton => DirectConversationFilterButton;

    private void OnSearchTextChanged(object sender, WpfControls.TextChangedEventArgs e) =>
        Raise("SearchTextChanged", sender, e);

    private void OnFilterClicked(object sender, RoutedEventArgs e) =>
        Raise("FilterRequested", sender, e);

    private void OnSelectionChanged(object sender, WpfControls.SelectionChangedEventArgs e) =>
        Raise("SelectionChanged", sender, e);

    private void OnCreateChannelClicked(object sender, RoutedEventArgs e) =>
        Raise("CreateChannelRequested", sender, e);

    private void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        Raise("SettingsRequested", sender, e);

    private void OnGroupLoaded(object sender, RoutedEventArgs e) =>
        Raise("GroupLoaded", sender, e);

    private void OnGroupExpanded(object sender, RoutedEventArgs e) =>
        Raise("GroupExpanded", sender, e);

    private void OnGroupCollapsed(object sender, RoutedEventArgs e) =>
        Raise("GroupCollapsed", sender, e);

    private void Raise(string interaction, object sender, object originalEventArgs) =>
        RaiseEvent(new ClientControlInteractionRequestedEventArgs(
            InteractionRequestedEvent,
            this,
            interaction,
            sender,
            originalEventArgs));
}
