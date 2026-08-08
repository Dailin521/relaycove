using System.Windows;
using System.Windows.Input;

namespace RelayCove.Client.Controls;

public partial class TitleBarControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(TitleBarControl),
        new PropertyMetadata("RelayCove"));

    public static readonly DependencyProperty IsMaximizedProperty = DependencyProperty.Register(
        nameof(IsMaximized),
        typeof(bool),
        typeof(TitleBarControl),
        new PropertyMetadata(false, OnIsMaximizedChanged));

    public static readonly RoutedEvent DragRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(DragRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TitleBarControl));

    public static readonly RoutedEvent MinimizeRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(MinimizeRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TitleBarControl));

    public static readonly RoutedEvent MaximizeRestoreRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(MaximizeRestoreRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TitleBarControl));

    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CloseRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TitleBarControl));

    public static readonly RoutedEvent SystemMenuRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SystemMenuRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(TitleBarControl));

    public TitleBarControl()
    {
        InitializeComponent();
        UpdateWindowStatePresentation();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsMaximized
    {
        get => (bool)GetValue(IsMaximizedProperty);
        set => SetValue(IsMaximizedProperty, value);
    }

    public event RoutedEventHandler DragRequested
    {
        add => AddHandler(DragRequestedEvent, value);
        remove => RemoveHandler(DragRequestedEvent, value);
    }

    public event RoutedEventHandler MinimizeRequested
    {
        add => AddHandler(MinimizeRequestedEvent, value);
        remove => RemoveHandler(MinimizeRequestedEvent, value);
    }

    public event RoutedEventHandler MaximizeRestoreRequested
    {
        add => AddHandler(MaximizeRestoreRequestedEvent, value);
        remove => RemoveHandler(MaximizeRestoreRequestedEvent, value);
    }

    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public event RoutedEventHandler SystemMenuRequested
    {
        add => AddHandler(SystemMenuRequestedEvent, value);
        remove => RemoveHandler(SystemMenuRequestedEvent, value);
    }

    private static void OnIsMaximizedChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((TitleBarControl)dependencyObject).UpdateWindowStatePresentation();
    }

    private void UpdateWindowStatePresentation()
    {
        if (MaximizeIcon is null || RestoreIcon is null)
        {
            return;
        }

        MaximizeIcon.Visibility = IsMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = IsMaximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        RaiseEvent(new RoutedEventArgs(
            e.ClickCount == 2 ? MaximizeRestoreRequestedEvent : DragRequestedEvent,
            this));
        e.Handled = true;
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        RaiseEvent(new RoutedEventArgs(SystemMenuRequestedEvent, this));
        e.Handled = true;
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        RaiseEvent(new RoutedEventArgs(MinimizeRequestedEvent, this));
        e.Handled = true;
    }

    private void OnMaximizeRestoreClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        RaiseEvent(new RoutedEventArgs(MaximizeRestoreRequestedEvent, this));
        e.Handled = true;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
        e.Handled = true;
    }
}
