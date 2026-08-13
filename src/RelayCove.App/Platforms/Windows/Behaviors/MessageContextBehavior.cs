using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using Windows.System;
using Windows.UI.Core;
using WinUiBorder = Microsoft.Maui.Platform.ContentPanel;
using WinUiElement = Microsoft.UI.Xaml.UIElement;
using WinUiListViewItem = Microsoft.UI.Xaml.Controls.ListViewItem;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class MessageContextBehavior : Behavior<Border>
{
    private WinUiBorder? _platformView;
    private WinUiElement? _inputSource;
    private Border? _virtualView;
    private ShellViewModel? _viewModel;
    private bool _opened;

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
        _inputSource = current as WinUiElement ?? platformView;
        _inputSource.RightTapped += OnRightTapped;
        _inputSource.KeyDown += OnKeyDown;
    }

    private void DetachInputSource()
    {
        if (_inputSource is null) return;
        _inputSource.RightTapped -= OnRightTapped;
        _inputSource.KeyDown -= OnKeyDown;
        _inputSource = null;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs eventArgs)
    {
        if (!Open()) return;
        eventArgs.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        var menuKey = eventArgs.Key == VirtualKey.Application;
        var shiftF10 = eventArgs.Key == VirtualKey.F10 && IsShiftPressed();
        if ((!menuKey && !shiftF10) || !Open()) return;
        eventArgs.Handled = true;
    }

    private bool Open()
    {
        var parameter = CommandParameter ?? _virtualView?.BindingContext;
        var command = Command;
        if (command is null)
        {
            _viewModel = ResolveViewModel();
            command = _viewModel?.OpenMessageMenuCommand;
        }
        if (command?.CanExecute(parameter) != true) return false;
        _opened = true;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        command.Execute(parameter);
        return true;
    }

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
}
