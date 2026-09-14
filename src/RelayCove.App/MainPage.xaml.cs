using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App;

public partial class MainPage : ContentPage
{
    private readonly ShellViewModel _viewModel;
    private readonly PointerEventHandler _messageMenuPointerPressedHandler;
    private FrameworkElement? _platformRoot;
    private Microsoft.Maui.Controls.Window? _activationWindow;
    private int _windowActivationRevision;

    public MainPage(ShellViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _messageMenuPointerPressedHandler = OnPagePointerPressed;
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += OnPageSizeChanged;
    }

    public ShellViewModel ViewModel => _viewModel;

    private void OnSearchCompleted(object? sender, EventArgs eventArgs) =>
        _viewModel.SearchNowCommand.Execute(null);

    private static void OnEmojiPointerEntered(object? sender, Microsoft.Maui.Controls.PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: EmojiChoice choice }) choice.IsPointerOver = true;
    }

    private static void OnEmojiPointerExited(object? sender, Microsoft.Maui.Controls.PointerEventArgs eventArgs)
    {
        if (sender is BindableObject { BindingContext: EmojiChoice choice }) choice.IsPointerOver = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AttachWindowActivation();
        RecheckWindowActivation();
        UpdateViewport();
        await _viewModel.InitializeAsync();
#if DEBUG
        if (NativeShellPreviewSession.IsRequested)
        {
            // WinUI reports its final per-monitor bounds after the first layout
            // pass. Re-apply the requested deterministic state once that pass
            // settles; this remains state injection, not keyboard/mouse input.
            await Task.Delay(300);
            _viewModel.ApplyNativePreviewTheme(NativeShellPreviewSession.RequestedTheme);
            _viewModel.ApplyNativePreviewScene(NativeShellPreviewSession.RequestedScene);
            await Task.Delay(100);
            _platformRoot?.InvalidateMeasure();
            _platformRoot?.UpdateLayout();
        }
#endif
    }

    protected override void OnDisappearing()
    {
        _windowActivationRevision++;
        _viewModel.SetWindowActive(false);
        DetachWindowActivation();
        base.OnDisappearing();
    }

    private void AttachWindowActivation()
    {
        var window = Window;
        if (ReferenceEquals(_activationWindow, window)) return;
        DetachWindowActivation();
        _activationWindow = window;
        if (_activationWindow is null) return;
        _activationWindow.Activated += OnWindowActivated;
        _activationWindow.Deactivated += OnWindowDeactivated;
    }

    private void DetachWindowActivation()
    {
        if (_activationWindow is null) return;
        _activationWindow.Activated -= OnWindowActivated;
        _activationWindow.Deactivated -= OnWindowDeactivated;
        _activationWindow = null;
    }

    private void OnWindowActivated(object? sender, EventArgs eventArgs) => RecheckWindowActivation();

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs)
    {
        _windowActivationRevision++;
        _viewModel.SetWindowActive(false);
    }

    private void RecheckWindowActivation()
    {
        var revision = ++_windowActivationRevision;
        _viewModel.SetWindowActive(false);
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            if (revision == _windowActivationRevision) _viewModel.SetWindowActive(true);
        });
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        _platformRoot?.RemoveHandler(UIElement.PointerPressedEvent, _messageMenuPointerPressedHandler);
        _platformRoot = null;
        base.OnHandlerChanging(args);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        _platformRoot = Handler?.PlatformView as FrameworkElement;
        _platformRoot?.AddHandler(UIElement.PointerPressedEvent, _messageMenuPointerPressedHandler, true);
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (!_viewModel.IsMessageMenuOpen) return;
        var menu = MessageMenuPopover.Handler?.PlatformView;
        for (var source = eventArgs.OriginalSource as DependencyObject;
             source is not null;
             source = VisualTreeHelper.GetParent(source))
        {
            if (ReferenceEquals(source, menu)) return;
        }

        // Dismiss outside presses without consuming them. The same right-click
        // must still reach the underlying message/image and open its menu.
        _viewModel.CloseMessageMenuCommand.Execute(null);
    }

    private void OnPageSizeChanged(object? sender, EventArgs eventArgs) => UpdateViewport();

    private void UpdateViewport()
    {
        var width = Width > 0 ? Width : 1440d;
        _viewModel.UpdateViewport(width, Height > 0 ? Height : 900d);
        _viewModel.ChannelSettings.UpdateViewport(width);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
#if DEBUG
        // Preview acceptance needs the same search-focus contract as production.
        // Other overlay focus remains suppressed so HWND resizing stays input-free.
        if (NativeShellPreviewSession.IsRequested &&
            eventArgs.PropertyName != nameof(ShellViewModel.IsSearchOpen)) return;
#endif
        switch (eventArgs.PropertyName)
        {
            case nameof(ShellViewModel.IsDetailsOpen):
                if (_viewModel.IsDetailsOpen && _viewModel.IsOverlayDetailsVisible)
                {
                    Dispatcher.Dispatch(OverlayDetails.FocusCloseButton);
                }
                else if (!_viewModel.IsDetailsOpen)
                {
                    Dispatcher.Dispatch(ChatHeader.FocusSettingsButton);
                }
                break;
            case nameof(ShellViewModel.IsSearchOpen) when _viewModel.IsSearchOpen:
                Dispatcher.Dispatch(() => SearchEntry.Focus());
                break;
            case nameof(ShellViewModel.IsSearchOpen):
                Dispatcher.Dispatch(ChatHeader.FocusSearchButton);
                break;
            case nameof(ShellViewModel.IsAccountMenuOpen) when _viewModel.IsAccountMenuOpen:
                Dispatcher.Dispatch(() => FirstAccountMenuButton.Focus());
                break;
            case nameof(ShellViewModel.IsOwnNameEditing) when _viewModel.IsOwnNameEditing:
                Dispatcher.Dispatch(() =>
                {
                    if (!_viewModel.IsOwnNameEditing || !_viewModel.IsAccountMenuOpen) return;
                    OwnNameEntry.Focus();
                    OwnNameEntry.CursorPosition = 0;
                    OwnNameEntry.SelectionLength = OwnNameEntry.Text?.Length ?? 0;
                });
                break;
            case nameof(ShellViewModel.IsDownloadCenterOpen) when _viewModel.IsDownloadCenterOpen:
                Dispatcher.Dispatch(() => DownloadCenterCloseButton.Focus());
                break;
            case nameof(ShellViewModel.IsNewConversationOpen) when _viewModel.IsNewConversationOpen:
                Dispatcher.Dispatch(() => NewConversationSearchEntry.Focus());
                break;
            case nameof(ShellViewModel.ChannelSettings) when _viewModel.ChannelSettings.IsOpen:
                Dispatcher.Dispatch(ChannelSettingsOverlay.FocusCloseButton);
                break;
            case nameof(ShellViewModel.IsComposerEmojiPickerOpen) when _viewModel.IsComposerEmojiPickerOpen:
                Dispatcher.Dispatch(() => ComposerEmojiCollection.Focus());
                break;
            case nameof(ShellViewModel.IsReactionPickerOpen) when _viewModel.IsReactionPickerOpen:
                Dispatcher.Dispatch(() => ReactionEmojiCollection.Focus());
                break;
            case nameof(ShellViewModel.IsImageViewerOpen) when _viewModel.IsImageViewerOpen:
                Dispatcher.Dispatch(() => ImageViewerCloseButton.Focus());
                break;
            case nameof(ShellViewModel.IsChannelMenuOpen) when _viewModel.IsChannelMenuOpen:
                Dispatcher.Dispatch(() => FirstChannelMenuButton.Focus());
                break;
            case nameof(ShellViewModel.IsTopicMenuOpen) when _viewModel.IsTopicMenuOpen:
                Dispatcher.Dispatch(() => FirstTopicMenuButton.Focus());
                break;
            case nameof(ShellViewModel.ChannelMenuFocusRequest):
                Dispatcher.Dispatch(ConversationPane.FocusChannelMenuButton);
                break;
            case nameof(ShellViewModel.TopicMenuFocusRequest):
                Dispatcher.Dispatch(ConversationPane.FocusTopicMenuButton);
                break;
            case nameof(ShellViewModel.IsEditDialogOpen) when _viewModel.IsEditDialogOpen:
                Dispatcher.Dispatch(() => EditMessageEditor.Focus());
                break;
            case nameof(ShellViewModel.IsDeleteConfirmationOpen) when _viewModel.IsDeleteConfirmationOpen:
                Dispatcher.Dispatch(() => DeleteCancelButton.Focus());
                break;
            case nameof(ShellViewModel.IsChannelUnsubscribeConfirmationOpen):
                if (_viewModel.IsChannelUnsubscribeConfirmationOpen)
                {
                    Dispatcher.Dispatch(() => ChannelUnsubscribeCancelButton.Focus());
                }
                else if (_viewModel.IsDetailsOpen)
                {
                    Dispatcher.Dispatch(() =>
                    {
                        if (_viewModel.IsOverlayDetailsVisible) OverlayDetails.FocusUnsubscribeButton();
                        else InlineDetails.FocusUnsubscribeButton();
                    });
                }
                break;
            case nameof(ShellViewModel.LogoutConfirmationVisible) when _viewModel.LogoutConfirmationVisible:
                Dispatcher.Dispatch(() => LogoutCancelButton.Focus());
                break;
        }
    }

}
