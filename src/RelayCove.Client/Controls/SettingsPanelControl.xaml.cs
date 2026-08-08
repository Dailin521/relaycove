using System.Windows;

namespace RelayCove.Client.Controls;

public partial class SettingsPanelControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty DisplayNameProperty = RegisterTextProperty(
        nameof(DisplayName),
        "尚未登录");

    public static readonly DependencyProperty ServerAddressProperty = RegisterTextProperty(
        nameof(ServerAddress),
        "—");

    public static readonly DependencyProperty ConnectionStatusProperty = RegisterTextProperty(
        nameof(ConnectionStatus),
        "实时连接：未连接");

    public static readonly DependencyProperty SyncStatusProperty = RegisterTextProperty(
        nameof(SyncStatus),
        "同步：尚未完成");

    public static readonly DependencyProperty NotificationStatusProperty = RegisterTextProperty(
        nameof(NotificationStatus),
        "系统通知：初始化中");

    public static readonly DependencyProperty UpdateStatusProperty = RegisterTextProperty(
        nameof(UpdateStatus),
        "更新：尚未检查");

    public static readonly DependencyProperty CanCheckForUpdatesProperty = RegisterBooleanProperty(
        nameof(CanCheckForUpdates));

    public static readonly DependencyProperty CanReconnectProperty = RegisterBooleanProperty(
        nameof(CanReconnect));

    public static readonly DependencyProperty CanExitAccountProperty = RegisterBooleanProperty(
        nameof(CanExitAccount));

    public static readonly RoutedEvent CloseRequestedEvent = RegisterRequestEvent(nameof(CloseRequested));

    public static readonly RoutedEvent CheckForUpdatesRequestedEvent = RegisterRequestEvent(
        nameof(CheckForUpdatesRequested));

    public static readonly RoutedEvent ReconnectRequestedEvent = RegisterRequestEvent(
        nameof(ReconnectRequested));

    public static readonly RoutedEvent ExitAccountRequestedEvent = RegisterRequestEvent(
        nameof(ExitAccountRequested));

    public SettingsPanelControl()
    {
        InitializeComponent();
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string ServerAddress
    {
        get => (string)GetValue(ServerAddressProperty);
        set => SetValue(ServerAddressProperty, value);
    }

    public string ConnectionStatus
    {
        get => (string)GetValue(ConnectionStatusProperty);
        set => SetValue(ConnectionStatusProperty, value);
    }

    public string SyncStatus
    {
        get => (string)GetValue(SyncStatusProperty);
        set => SetValue(SyncStatusProperty, value);
    }

    public string NotificationStatus
    {
        get => (string)GetValue(NotificationStatusProperty);
        set => SetValue(NotificationStatusProperty, value);
    }

    public string UpdateStatus
    {
        get => (string)GetValue(UpdateStatusProperty);
        set => SetValue(UpdateStatusProperty, value);
    }

    public bool CanCheckForUpdates
    {
        get => (bool)GetValue(CanCheckForUpdatesProperty);
        set => SetValue(CanCheckForUpdatesProperty, value);
    }

    public bool CanReconnect
    {
        get => (bool)GetValue(CanReconnectProperty);
        set => SetValue(CanReconnectProperty, value);
    }

    public bool CanExitAccount
    {
        get => (bool)GetValue(CanExitAccountProperty);
        set => SetValue(CanExitAccountProperty, value);
    }

    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public event RoutedEventHandler CheckForUpdatesRequested
    {
        add => AddHandler(CheckForUpdatesRequestedEvent, value);
        remove => RemoveHandler(CheckForUpdatesRequestedEvent, value);
    }

    public event RoutedEventHandler ReconnectRequested
    {
        add => AddHandler(ReconnectRequestedEvent, value);
        remove => RemoveHandler(ReconnectRequestedEvent, value);
    }

    public event RoutedEventHandler ExitAccountRequested
    {
        add => AddHandler(ExitAccountRequestedEvent, value);
        remove => RemoveHandler(ExitAccountRequestedEvent, value);
    }

    internal System.Windows.Controls.Button CloseButton => CloseButtonElement;

    internal System.Windows.Controls.Button CheckForUpdatesButton => CheckForUpdatesButtonElement;

    internal System.Windows.Controls.Button ReconnectButton => ReconnectButtonElement;

    internal System.Windows.Controls.Button ExitAccountButton => ExitAccountButtonElement;

    private static DependencyProperty RegisterTextProperty(string name, string defaultValue) =>
        DependencyProperty.Register(name, typeof(string), typeof(SettingsPanelControl), new PropertyMetadata(defaultValue));

    private static DependencyProperty RegisterBooleanProperty(string name) =>
        DependencyProperty.Register(name, typeof(bool), typeof(SettingsPanelControl), new PropertyMetadata(false));

    private static RoutedEvent RegisterRequestEvent(string name) =>
        EventManager.RegisterRoutedEvent(
            name,
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(SettingsPanelControl));

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
    }

    private void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(CheckForUpdatesRequestedEvent, this));
    }

    private void OnReconnectClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(ReconnectRequestedEvent, this));
    }

    private void OnExitAccountClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(ExitAccountRequestedEvent, this));
    }
}
