using System.Windows;
using RelayCove.Client.Presentation;

namespace RelayCove.Client.Controls;

public partial class NavigationRailControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SelectedSectionProperty =
        DependencyProperty.Register(
            nameof(SelectedSection),
            typeof(ClientNavigationSection),
            typeof(NavigationRailControl),
            new PropertyMetadata(ClientNavigationSection.Chat, OnSelectedSectionChanged));

    public static readonly DependencyProperty AvatarTextProperty = DependencyProperty.Register(
        nameof(AvatarText),
        typeof(string),
        typeof(NavigationRailControl),
        new PropertyMetadata("RC"));

    public static readonly RoutedEvent NavigationRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(NavigationRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ClientNavigationRequestedEventArgs>),
        typeof(NavigationRailControl));

    public static readonly RoutedEvent UnavailableFeatureRequestedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(UnavailableFeatureRequested),
            RoutingStrategy.Bubble,
            typeof(EventHandler<ClientUnavailableFeatureRequestedEventArgs>),
            typeof(NavigationRailControl));

    public NavigationRailControl()
    {
        InitializeComponent();
        UpdateSelectionPresentation();
    }

    public ClientNavigationSection SelectedSection
    {
        get => (ClientNavigationSection)GetValue(SelectedSectionProperty);
        set => SetValue(SelectedSectionProperty, value);
    }

    public string AvatarText
    {
        get => (string)GetValue(AvatarTextProperty);
        set => SetValue(AvatarTextProperty, value);
    }

    public event EventHandler<ClientNavigationRequestedEventArgs> NavigationRequested
    {
        add => AddHandler(NavigationRequestedEvent, value);
        remove => RemoveHandler(NavigationRequestedEvent, value);
    }

    public event EventHandler<ClientUnavailableFeatureRequestedEventArgs>
        UnavailableFeatureRequested
    {
        add => AddHandler(UnavailableFeatureRequestedEvent, value);
        remove => RemoveHandler(UnavailableFeatureRequestedEvent, value);
    }

    private static void OnSelectedSectionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((NavigationRailControl)dependencyObject).UpdateSelectionPresentation();
    }

    private void UpdateSelectionPresentation()
    {
        if (ChatButton is null || ChannelsButton is null || SettingsButton is null)
        {
            return;
        }

        UpdateButtonSelection(ChatButton, SelectedSection == ClientNavigationSection.Chat);
        UpdateButtonSelection(
            ChannelsButton,
            SelectedSection == ClientNavigationSection.Channels);
        UpdateButtonSelection(
            SettingsButton,
            SelectedSection == ClientNavigationSection.Settings);
    }

    private static void UpdateButtonSelection(
        System.Windows.Controls.Button button,
        bool selected)
    {
        button.SetResourceReference(
            BackgroundProperty,
            selected ? "RcPrimarySoftBrush" : "RcTransparentBrush");
        button.SetResourceReference(
            ForegroundProperty,
            selected ? "RcPrimaryBrush" : "RcTextSecondaryBrush");
    }

    private void OnAccountClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        RequestNavigation(ClientNavigationSection.Settings);
        e.Handled = true;
    }

    private void OnNavigationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button
            {
                Tag: ClientNavigationSection section,
            })
        {
            RequestNavigation(section);
        }

        e.Handled = true;
    }

    private void RequestNavigation(ClientNavigationSection section)
    {
        RaiseEvent(new ClientNavigationRequestedEventArgs(
            NavigationRequestedEvent,
            this,
            section));
    }

    private void OnUnavailableClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button
            {
                Tag: ClientUiFeatureId featureId,
            } button)
        {
            var displayName = featureId switch
            {
                ClientUiFeatureId.Contacts => "联系人",
                ClientUiFeatureId.NotificationCenter => "通知中心",
                ClientUiFeatureId.FileCenter => "文件中心",
                ClientUiFeatureId.MoreNavigation => "更多功能",
                _ => "该功能",
            };
            RaiseEvent(new ClientUnavailableFeatureRequestedEventArgs(
                UnavailableFeatureRequestedEvent,
                this,
                featureId,
                displayName));
        }

        e.Handled = true;
    }
}
