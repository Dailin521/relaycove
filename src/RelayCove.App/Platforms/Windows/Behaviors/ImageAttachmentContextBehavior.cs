using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using WinUiBorder = Microsoft.Maui.Platform.ContentPanel;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ImageAttachmentContextBehavior : Behavior<Border>
{
    private WinUiBorder? _platformView;
    private Border? _virtualView;
    private ShellViewModel? _viewModel;
    private bool _opened;

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

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        AttachNativeView((sender as Border)?.Handler?.PlatformView as WinUiBorder);

    private void AttachNativeView(WinUiBorder? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;
        _platformView = platformView;
        platformView.RightTapped += OnRightTapped;
    }

    private void DetachNativeView()
    {
        if (_platformView is not null) _platformView.RightTapped -= OnRightTapped;
        StopWatchingFocusReturn();
        _platformView = null;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs eventArgs)
    {
        if (_virtualView?.BindingContext is not MessageAttachmentItem { IsImage: true } attachment ||
            ResolveMessage() is not { MessageId: not null } message ||
            ResolveViewModel() is not { } viewModel ||
            GetPageRoot() is not { } pageRoot)
        {
            return;
        }

        var anchor = eventArgs.GetPosition(pageRoot);
        var request = new ImageAttachmentMenuRequest(message, attachment, anchor.X, anchor.Y);
        if (!viewModel.OpenImageAttachmentMenuAtCommand.CanExecute(request)) return;

        _viewModel = viewModel;
        _opened = true;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.OpenImageAttachmentMenuAtCommand.Execute(request);
        eventArgs.Handled = true;
    }

    private MessageItem? ResolveMessage()
    {
        for (Element? current = _virtualView?.Parent; current is not null; current = current.Parent)
        {
            if (current.BindingContext is MessageItem message) return message;
        }
        return null;
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

    private static WinUiFrameworkElement? GetPageRoot() => Application.Current?.Windows
        .Select(window => window.Page?.Handler?.PlatformView)
        .OfType<WinUiFrameworkElement>()
        .FirstOrDefault();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (!_opened || eventArgs.PropertyName != nameof(ShellViewModel.MessageActionFocusRequest) ||
            _platformView is null)
        {
            return;
        }

        _platformView.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        StopWatchingFocusReturn();
    }

    private void StopWatchingFocusReturn()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        _opened = false;
    }
}
