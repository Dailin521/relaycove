using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using WinElement = Microsoft.UI.Xaml.FrameworkElement;
using WinUiElement = Microsoft.UI.Xaml.UIElement;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using MenuFlyoutSeparator = Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ConversationContextBehavior : Behavior<Microsoft.Maui.Controls.Border>
{
    private Microsoft.Maui.Controls.Border? _view;
    private WinElement? _platformView;
    private WinUiElement? _inputSource;
    private MenuFlyout? _menu;
    private readonly RightTappedEventHandler _rightTappedHandler;

    public ConversationContextBehavior() => _rightTappedHandler = OnRightTapped;

    protected override void OnAttachedTo(Microsoft.Maui.Controls.Border bindable)
    {
        base.OnAttachedTo(bindable);
        _view = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        bindable.BindingContextChanged += OnBindingContextChanged;
        AttachNativeView();
    }

    protected override void OnDetachingFrom(Microsoft.Maui.Controls.Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        bindable.BindingContextChanged -= OnBindingContextChanged;
        DetachNativeView();
        _view = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs args) => AttachNativeView();
    private void OnBindingContextChanged(object? sender, EventArgs args) => _menu?.Hide();

    private void AttachNativeView()
    {
        DetachNativeView();
        if (_view?.Handler?.PlatformView is not WinElement platformView) return;
        _platformView = platformView;
        platformView.Loaded += OnLoaded;
        platformView.Unloaded += OnUnloaded;
        AttachInputSource();
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args) => AttachInputSource();

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        _menu?.Hide();
        _inputSource?.RemoveHandler(WinUiElement.RightTappedEvent, _rightTappedHandler);
        _inputSource = null;
    }

    private void AttachInputSource()
    {
        _inputSource?.RemoveHandler(WinUiElement.RightTappedEvent, _rightTappedHandler);
        Microsoft.UI.Xaml.DependencyObject? ancestor = _platformView;
        while (ancestor is not null and not ListViewItem)
            ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor);
        _inputSource = ancestor as WinUiElement ?? _platformView;
        _inputSource?.AddHandler(WinUiElement.RightTappedEvent, _rightTappedHandler, true);
    }

    private void DetachNativeView()
    {
        _menu?.Hide();
        _menu = null;
        _inputSource?.RemoveHandler(WinUiElement.RightTappedEvent, _rightTappedHandler);
        _inputSource = null;
        if (_platformView is not null)
        {
            _platformView.Loaded -= OnLoaded;
            _platformView.Unloaded -= OnUnloaded;
        }
        _platformView = null;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (_platformView is null || _view?.BindingContext is not ConversationListItem item) return;
        ShellViewModel? viewModel = null;
        for (Element? ancestor = _view; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.BindingContext is not ShellViewModel shell) continue;
            viewModel = shell;
            break;
        }
        if (viewModel?.CreateConversationMenuTarget(item) is not { } target) return;
        _menu?.Hide();
        var menu = _menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = target.IsPinned ? "取消置顶" : "置顶",
            Command = viewModel.ToggleConversationPinnedCommand,
            CommandParameter = target
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = target.IsMuted ? "取消免打扰" : "免打扰",
            Command = viewModel.ToggleConversationMutedCommand,
            CommandParameter = target
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "删除聊天",
            Command = viewModel.DeleteConversationCommand,
            CommandParameter = target
        });
        menu.ShowAt(_platformView, new FlyoutShowOptions { Position = args.GetPosition(_platformView) });
        args.Handled = true;
    }
}
