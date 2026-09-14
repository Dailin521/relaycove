using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using WinPoint = Windows.Foundation.Point;
using WinUiBorder = Microsoft.Maui.Platform.ContentPanel;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;
using WinUiListViewItem = Microsoft.UI.Xaml.Controls.ListViewItem;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class MessageContextBehavior : Behavior<Border>
{
    private WinUiBorder? _platformView;
    private Microsoft.UI.Xaml.UIElement? _inputSource;
    private Border? _virtualView;
    private readonly RightTappedEventHandler _rightTappedHandler;

    public MessageContextBehavior() => _rightTappedHandler = OnRightTapped;

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(MessageContextBehavior));

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter),
        typeof(object),
        typeof(MessageContextBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    protected override void OnAttachedTo(Border bindable)
    {
        base.OnAttachedTo(bindable);
        _virtualView = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        AttachNativeView(bindable.Handler?.PlatformView as WinUiBorder);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        DetachNativeView();
        _virtualView = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs)
    {
        AttachNativeView((sender as Border)?.Handler?.PlatformView as WinUiBorder);
    }

    private void AttachNativeView(WinUiBorder? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;

        _platformView = platformView;
        platformView.IsTabStop = false;
        platformView.Loaded += OnPlatformViewLoaded;
        AttachInputSource(platformView);
    }

    private void DetachNativeView()
    {
        var platformView = _platformView;
        if (platformView is null) return;

        platformView.Loaded -= OnPlatformViewLoaded;
        DetachInputSource();
        _platformView = null;
    }

    private void OnPlatformViewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs eventArgs)
    {
        if (_platformView is not null) AttachInputSource(_platformView);
    }

    private void AttachInputSource(WinUiBorder platformView)
    {
        DetachInputSource();
        Microsoft.UI.Xaml.DependencyObject? current = platformView;
        while (current is not null and not WinUiListViewItem)
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        _inputSource = current as Microsoft.UI.Xaml.UIElement ?? platformView;
        _inputSource.AddHandler(Microsoft.UI.Xaml.UIElement.RightTappedEvent, _rightTappedHandler, true);
    }

    private void DetachInputSource()
    {
        if (_inputSource is null) return;
        _inputSource.RemoveHandler(Microsoft.UI.Xaml.UIElement.RightTappedEvent, _rightTappedHandler);
        _inputSource = null;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs eventArgs)
    {
        var viewModel = ResolveViewModel();
        var parameter = CommandParameter ?? _virtualView?.BindingContext;
        // The image's own handler runs first. Keep its attachment-specific menu
        // when this same routed event reaches the enclosing message row.
        if (eventArgs.Handled && viewModel is { IsMessageMenuOpen: true, HasActiveMessageAttachment: true } &&
            ReferenceEquals(viewModel.ActiveMessageAction, parameter)) return;
        var pageRoot = GetPageRoot();
        WinPoint? anchor = pageRoot is null ? null : eventArgs.GetPosition(pageRoot);
        if (!Open(viewModel, parameter, anchor)) return;
        eventArgs.Handled = true;
    }

    private bool Open(ShellViewModel? viewModel, object? parameter, WinPoint? anchor)
    {
        var command = Command;
        if (command is null && viewModel is not null && parameter is MessageItem message)
        {
            var position = anchor ?? GetDefaultAnchor(message);
            parameter = new MessageMenuRequest(message, position.X, position.Y);
            command = viewModel.OpenMessageMenuAtCommand;
        }
        else if (command is null) command = viewModel?.OpenMessageMenuCommand;
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }

    private WinPoint GetDefaultAnchor(MessageItem message)
    {
        var source = _virtualView?.Handler?.PlatformView as WinUiFrameworkElement ??
            _inputSource as WinUiFrameworkElement;
        var pageRoot = GetPageRoot();
        if (source is null || pageRoot is null) return new WinPoint(12d, 68d);
        var localX = message.IsOwn ? 0d : source.ActualWidth;
        return source.TransformToVisual(pageRoot)
            .TransformPoint(new WinPoint(localX, Math.Min(source.ActualHeight, 36d)));
    }

    private static WinUiFrameworkElement? GetPageRoot() => Application.Current?.Windows
        .Select(window => window.Page?.Handler?.PlatformView)
        .OfType<WinUiFrameworkElement>()
        .FirstOrDefault();

    private ShellViewModel? ResolveViewModel()
    {
        for (Element? current = _virtualView; current is not null; current = current.Parent)
        {
            if (current.BindingContext is ShellViewModel viewModel) return viewModel;
        }
        return Application.Current?.Windows
            .Select(window => window.Page?.BindingContext)
            .OfType<ShellViewModel>()
            .FirstOrDefault();
    }

}
