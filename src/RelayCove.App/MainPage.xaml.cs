using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.ViewModels;
using Windows.System;

namespace RelayCove.App;

public partial class MainPage : ContentPage
{
    private readonly ShellViewModel _viewModel;
    private FrameworkElement? _platformRoot;

    public MainPage(ShellViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += OnPageSizeChanged;
    }

    public ShellViewModel ViewModel => _viewModel;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateViewport();
        await _viewModel.InitializeAsync();
    }

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
            case nameof(ShellViewModel.IsNewConversationOpen) when _viewModel.IsNewConversationOpen:
                Dispatcher.Dispatch(() => NewConversationSearchEntry.Focus());
                break;
            case nameof(ShellViewModel.IsComposerEmojiPickerOpen) when _viewModel.IsComposerEmojiPickerOpen:
                Dispatcher.Dispatch(() =>
                {
                    _viewModel.SelectedComposerEmoji ??= _viewModel.EmojiChoices[0];
                    ComposerEmojiCollection.Focus();
                });
                break;
            case nameof(ShellViewModel.IsReactionPickerOpen) when _viewModel.IsReactionPickerOpen:
                Dispatcher.Dispatch(() =>
                {
                    _viewModel.SelectedReactionEmoji ??= _viewModel.EmojiChoices[0];
                    ReactionEmojiCollection.Focus();
                });
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
            _viewModel.IsMessageMenuOpen && HandleMessageMenuKey(eventArgs.Key))
        {
            eventArgs.Handled = true;
        }
    }

    private bool CloseTopOverlay()
    {
        if (_viewModel.IsNewConversationOpen) _viewModel.CloseNewConversationCommand.Execute(null);
        else if (_viewModel.IsImageViewerOpen) _viewModel.CloseImageViewerCommand.Execute(null);
        else if (_viewModel.IsDeleteConfirmationOpen) _viewModel.CancelDeleteMessageCommand.Execute(null);
        else if (_viewModel.IsEditDialogOpen) _viewModel.CancelEditDialogCommand.Execute(null);
        else if (_viewModel.IsReactionPickerOpen) _viewModel.CloseReactionPickerCommand.Execute(null);
        else if (_viewModel.IsMessageMenuOpen) _viewModel.CloseMessageMenuCommand.Execute(null);
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
        if (key == VirtualKey.Enter)
        {
            _viewModel.SelectSearchResultCommand.Execute(_viewModel.SelectedSearchResult ?? _viewModel.SearchResults[0]);
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
        var current = selected is null ? 0 : IndexOfEmoji(selected);
        if (key == VirtualKey.Enter)
        {
            if (reaction) _viewModel.SelectReactionEmojiCommand.Execute(selected ?? _viewModel.EmojiChoices[0]);
            else _viewModel.InsertComposerEmojiCommand.Execute(selected ?? _viewModel.EmojiChoices[0]);
            return true;
        }
        var next = key switch
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
        if (key == VirtualKey.Home)
        {
            FirstMessageMenuButton.Focus();
            return true;
        }
        if (key == VirtualKey.End)
        {
            (_viewModel.CanDeleteActiveMessage ? LastMessageMenuButton : StarMessageMenuButton).Focus();
            return true;
        }
        if (key is not (VirtualKey.Up or VirtualKey.Down)) return false;
        FocusManager.TryMoveFocus(key == VirtualKey.Up
            ? FocusNavigationDirection.Up
            : FocusNavigationDirection.Down);
        return true;
    }
}
