using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using Windows.System;

namespace RelayCove.App;

public partial class MainPage : ContentPage
{
    private readonly ShellViewModel _viewModel;
    private FrameworkElement? _platformRoot;
    private Microsoft.Maui.Controls.Window? _activationWindow;

    public MainPage(ShellViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
        _viewModel.SetWindowActive(true);
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
        }
#endif
    }

    protected override void OnDisappearing()
    {
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

    private void OnWindowActivated(object? sender, EventArgs eventArgs) => _viewModel.SetWindowActive(true);

    private void OnWindowDeactivated(object? sender, EventArgs eventArgs) => _viewModel.SetWindowActive(false);

    protected override void OnHandlerChanged()
    {
        if (_platformRoot is not null) _platformRoot.KeyDown -= OnPlatformKeyDown;
        base.OnHandlerChanged();
        _platformRoot = Handler?.PlatformView as FrameworkElement;
        if (_platformRoot is not null) _platformRoot.KeyDown += OnPlatformKeyDown;
    }

    private void OnPageSizeChanged(object? sender, EventArgs eventArgs) => UpdateViewport();

    private void UpdateViewport()
    {
        var width = Width > 0 ? Width : 1440d;
        _viewModel.UpdateViewport(width);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
#if DEBUG
        // Deterministic preview scenes are state-driven and intentionally avoid
        // synthesizing focus/input while the screenshot harness resizes HWNDs.
        if (NativeShellPreviewSession.IsRequested) return;
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
                    Dispatcher.Dispatch(ChatHeader.FocusDetailsButton);
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
            case nameof(ShellViewModel.IsNewConversationOpen) when _viewModel.IsNewConversationOpen:
                Dispatcher.Dispatch(() => NewConversationSearchEntry.Focus());
                break;
            case nameof(ShellViewModel.IsChannelBrowserOpen) when _viewModel.IsChannelBrowserOpen:
                Dispatcher.Dispatch(() => ChannelBrowserCloseButton.Focus());
                break;
            case nameof(ShellViewModel.IsChannelBrowserOpen):
                Dispatcher.Dispatch(ConversationPane.FocusBrowseChannelsButton);
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
            case nameof(ShellViewModel.IsMessageMenuOpen) when _viewModel.IsMessageMenuOpen:
                Dispatcher.Dispatch(() => FirstMessageMenuButton.Focus());
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

    private void OnPlatformKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key == VirtualKey.Escape && CloseTopOverlay())
        {
            eventArgs.Handled = true;
            return;
        }

        if (_viewModel.IsSearchOpen && HandleSearchKey(eventArgs.Key) ||
            _viewModel.IsComposerEmojiPickerOpen && HandleEmojiKey(eventArgs.Key, reaction: false) ||
            _viewModel.IsReactionPickerOpen && HandleEmojiKey(eventArgs.Key, reaction: true) ||
            _viewModel.IsAccountMenuOpen && HandleAccountMenuKey(eventArgs.Key) ||
            _viewModel.IsMessageMenuOpen && HandleMessageMenuKey(eventArgs.Key))
        {
            eventArgs.Handled = true;
        }
    }

    private bool CloseTopOverlay()
    {
        if (_viewModel.IsChannelBrowserOpen) _viewModel.CloseChannelBrowserCommand.Execute(null);
        else if (_viewModel.IsNewConversationOpen) _viewModel.CloseNewConversationCommand.Execute(null);
        else if (_viewModel.IsImageViewerOpen) _viewModel.CloseImageViewerCommand.Execute(null);
        else if (_viewModel.IsChannelUnsubscribeConfirmationOpen) _viewModel.CancelChannelUnsubscribeCommand.Execute(null);
        else if (_viewModel.IsDeleteConfirmationOpen) _viewModel.CancelDeleteMessageCommand.Execute(null);
        else if (_viewModel.IsEditDialogOpen) _viewModel.CancelEditDialogCommand.Execute(null);
        else if (_viewModel.IsReactionPickerOpen) _viewModel.CloseReactionPickerCommand.Execute(null);
        else if (_viewModel.IsMessageMenuOpen) _viewModel.CloseMessageMenuCommand.Execute(null);
        else if (_viewModel.IsAccountMenuOpen)
        {
            _viewModel.CloseAccountMenuCommand.Execute(null);
            NavigationRail.FocusAccountButton();
        }
        else if (_viewModel.IsComposerEmojiPickerOpen) _viewModel.ToggleComposerEmojiPickerCommand.Execute(null);
        else if (_viewModel.IsSearchOpen) _viewModel.CloseSearchCommand.Execute(null);
        else if (_viewModel.LogoutConfirmationVisible) _viewModel.CancelLogoutCommand.Execute(null);
        else if (_viewModel.IsOverlayDetailsVisible) _viewModel.ToggleDetailsCommand.Execute(null);
        else return false;
        return true;
    }

    private bool HandleSearchKey(VirtualKey key)
    {
        if (_viewModel.SearchResults.Count == 0) return false;
        if (key == VirtualKey.Enter && _viewModel.SelectedSearchResult is not null)
        {
            _viewModel.SelectSearchResultCommand.Execute(_viewModel.SelectedSearchResult);
            return true;
        }
        var current = _viewModel.SelectedSearchResult is { } selected
            ? _viewModel.SearchResults.IndexOf(selected)
            : -1;
        var next = key switch
        {
            VirtualKey.Up => Math.Max(0, current - 1),
            VirtualKey.Down => Math.Min(_viewModel.SearchResults.Count - 1, current + 1),
            VirtualKey.Home => 0,
            VirtualKey.End => _viewModel.SearchResults.Count - 1,
            _ => -1
        };
        if (next < 0) return false;
        _viewModel.SelectedSearchResult = _viewModel.SearchResults[next];
        return true;
    }

    private bool HandleEmojiKey(VirtualKey key, bool reaction)
    {
        var selected = reaction ? _viewModel.SelectedReactionEmoji : _viewModel.SelectedComposerEmoji;
        var current = selected is null ? -1 : IndexOfEmoji(selected);
        if (key == VirtualKey.Enter)
        {
            if (reaction) _viewModel.SelectReactionEmojiCommand.Execute(selected ?? _viewModel.EmojiChoices[0]);
            else _viewModel.InsertComposerEmojiCommand.Execute(selected ?? _viewModel.EmojiChoices[0]);
            return true;
        }
        var next = selected is null && key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            ? 0
            : key switch
            {
                VirtualKey.Left => Math.Max(0, current - 1),
                VirtualKey.Right => Math.Min(_viewModel.EmojiChoices.Count - 1, current + 1),
                VirtualKey.Up => Math.Max(0, current - 6),
                VirtualKey.Down => Math.Min(_viewModel.EmojiChoices.Count - 1, current + 6),
                VirtualKey.Home => 0,
                VirtualKey.End => _viewModel.EmojiChoices.Count - 1,
                _ => -1
            };
        if (next < 0) return false;
        var choice = _viewModel.EmojiChoices[next];
        if (reaction)
        {
            _viewModel.SelectedReactionEmoji = choice;
            ReactionEmojiCollection.ScrollTo(choice);
        }
        else
        {
            _viewModel.SelectedComposerEmoji = choice;
            ComposerEmojiCollection.ScrollTo(choice);
        }
        return true;
    }

    private int IndexOfEmoji(EmojiChoice selected)
    {
        for (var index = 0; index < _viewModel.EmojiChoices.Count; index++)
        {
            if (Equals(_viewModel.EmojiChoices[index], selected)) return index;
        }
        return 0;
    }

    private bool HandleMessageMenuKey(VirtualKey key)
    {
        if (key == VirtualKey.Tab)
        {
            _viewModel.CloseMessageMenuCommand.Execute(null);
            return true;
        }
        if (key == VirtualKey.Home)
        {
            FirstMessageMenuButton.Focus();
            return true;
        }
        if (key == VirtualKey.End)
        {
            LastMessageMenuButton.Focus();
            return true;
        }
        if (key is not (VirtualKey.Up or VirtualKey.Down)) return false;
        FocusManager.TryMoveFocus(key == VirtualKey.Up
            ? FocusNavigationDirection.Up
            : FocusNavigationDirection.Down);
        return true;
    }

    private bool HandleAccountMenuKey(VirtualKey key)
    {
        if (key == VirtualKey.Tab)
        {
            _viewModel.CloseAccountMenuCommand.Execute(null);
            NavigationRail.FocusAccountButton();
            return true;
        }
        if (key == VirtualKey.Home)
        {
            FirstAccountMenuButton.Focus();
            return true;
        }
        if (key == VirtualKey.End)
        {
            LastAccountMenuButton.Focus();
            return true;
        }
        if (key is not (VirtualKey.Up or VirtualKey.Down)) return false;
        FocusManager.TryMoveFocus(key == VirtualKey.Up
            ? FocusNavigationDirection.Up
            : FocusNavigationDirection.Down);
        return true;
    }
}
