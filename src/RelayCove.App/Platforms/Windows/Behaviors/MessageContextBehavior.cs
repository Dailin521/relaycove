using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using Windows.System;
using Windows.UI.Core;
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
    private ShellViewModel? _viewModel;
    private bool _opened;
    private bool _pointerOver;
    private bool _keyboardFocused;

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(MessageContextBehavior));

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter),
        typeof(object),
        typeof(MessageContextBehavior));

    public static readonly BindableProperty FocusRequestProperty = BindableProperty.Create(
        nameof(FocusRequest),
        typeof(int),
        typeof(MessageContextBehavior),
        0,
        propertyChanged: OnFocusRequestChanged);

    public static readonly BindableProperty RevealElementProperty = BindableProperty.Create(
        nameof(RevealElement),
        typeof(VisualElement),
        typeof(MessageContextBehavior),
        propertyChanged: OnRevealElementChanged);

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

    public int FocusRequest
    {
        get => (int)GetValue(FocusRequestProperty);
        set => SetValue(FocusRequestProperty, value);
    }

    public VisualElement? RevealElement
    {
        get => (VisualElement?)GetValue(RevealElementProperty);
        set => SetValue(RevealElementProperty, value);
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
        platformView.IsTabStop = true;
        platformView.Loaded += OnPlatformViewLoaded;
        AttachInputSource(platformView);
    }

    private void DetachNativeView()
    {
        var platformView = _platformView;
        if (platformView is null) return;

        platformView.Loaded -= OnPlatformViewLoaded;
        DetachInputSource();
        StopWatchingFocusReturn();
        _platformView = null;
        _opened = false;
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
        _inputSource.RightTapped += OnRightTapped;
        _inputSource.KeyDown += OnKeyDown;
        _inputSource.PointerEntered += OnPointerEntered;
        _inputSource.PointerExited += OnPointerExited;
        _inputSource.GotFocus += OnGotFocus;
        _inputSource.LostFocus += OnLostFocus;
    }

    private void DetachInputSource()
    {
        if (_inputSource is null) return;
        _inputSource.RightTapped -= OnRightTapped;
        _inputSource.KeyDown -= OnKeyDown;
        _inputSource.PointerEntered -= OnPointerEntered;
        _inputSource.PointerExited -= OnPointerExited;
        _inputSource.GotFocus -= OnGotFocus;
        _inputSource.LostFocus -= OnLostFocus;
        _inputSource = null;
        _pointerOver = false;
        _keyboardFocused = false;
        UpdateRevealState();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs eventArgs)
    {
        _pointerOver = true;
        UpdateRevealState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs eventArgs)
    {
        _pointerOver = false;
        UpdateRevealState();
    }

    private void OnGotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs eventArgs)
    {
        _keyboardFocused = true;
        UpdateRevealState();
    }

    private void OnLostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs eventArgs)
    {
        _keyboardFocused = false;
        UpdateRevealState();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs eventArgs)
    {
        var pageRoot = GetPageRoot();
        WinPoint? anchor = pageRoot is null ? null : eventArgs.GetPosition(pageRoot);
        if (!Open(anchor)) return;
        eventArgs.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        var menuKey = eventArgs.Key == VirtualKey.Application;
        var shiftF10 = eventArgs.Key == VirtualKey.F10 && IsShiftPressed();
        if ((!menuKey && !shiftF10) || !Open()) return;
        eventArgs.Handled = true;
    }

    private bool Open(WinPoint? anchor = null)
    {
        var parameter = CommandParameter ?? _virtualView?.BindingContext;
        var command = Command;
        _viewModel = ResolveViewModel();
        if (command is null && _viewModel is not null && parameter is MessageItem message)
        {
            var position = anchor ?? GetDefaultAnchor(message);
            parameter = new MessageMenuRequest(message, position.X, position.Y);
            command = _viewModel.OpenMessageMenuAtCommand;
        }
        else if (command is null) command = _viewModel?.OpenMessageMenuCommand;
        if (command?.CanExecute(parameter) != true) return false;
        _opened = true;
        UpdateRevealState();
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (!_opened || eventArgs.PropertyName != nameof(ShellViewModel.MessageActionFocusRequest) ||
            _inputSource is null) return;
        _inputSource.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        StopWatchingFocusReturn();
    }

    private void StopWatchingFocusReturn()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        _opened = false;
        UpdateRevealState();
    }

    private static bool IsShiftPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static void OnFocusRequestChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var behavior = (MessageContextBehavior)bindable;
        if (!behavior._opened || behavior._inputSource is null) return;
        behavior._opened = false;
        behavior._inputSource.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    private static void OnRevealElementChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((MessageContextBehavior)bindable).UpdateRevealState();

    private void UpdateRevealState()
    {
        var reveal = RevealElement;
        if (reveal is null) return;
        var visible = _pointerOver || _keyboardFocused || _opened;
        reveal.Dispatcher.Dispatch(() =>
        {
            reveal.Opacity = visible ? 1d : 0d;
            reveal.IsEnabled = visible;
        });
    }
}
