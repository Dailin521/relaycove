using System.ComponentModel;
using System.Windows.Input;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class MessageListView : ContentView
{
    private ShellViewModel? _viewModel;
    private VisualElement? _messageMenuTrigger;

    public MessageListView()
    {
        InitializeComponent();
    }

    protected override void OnBindingContextChanged()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnBindingContextChanged();
        _viewModel = BindingContext as ShellViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ShellViewModel.MessageActionFocusRequest) ||
            _messageMenuTrigger is null) return;
        Dispatcher.Dispatch(() =>
        {
            _messageMenuTrigger?.Focus();
            _messageMenuTrigger = null;
        });
    }

    private void OnOpenMessageMenuClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { BindingContext: MessageItem message } button) return;
        _messageMenuTrigger = button;
        Execute(_viewModel?.OpenMessageMenuCommand, message);
    }

    private void OnQuoteMessageClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.QuoteMessageCommand);

    private void OnCopyMessageClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.CopyMessageRawCommand);

    private void OnOpenReactionPickerClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.OpenReactionPickerCommand);

    private void OnToggleReactionClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is Button { BindingContext: ReactionItem reaction })
            Execute(_viewModel?.ToggleReactionCommand, reaction);
    }

    private void OnDownloadAttachmentClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is Button { BindingContext: MessageAttachmentItem attachment })
            Execute(_viewModel?.DownloadAttachmentCommand, attachment);
    }

    private void OnImageTapped(object? sender, TappedEventArgs eventArgs)
    {
        var attachment = eventArgs.Parameter as MessageAttachmentItem ??
            (sender as BindableObject)?.BindingContext as MessageAttachmentItem;
        Execute(_viewModel?.OpenImageViewerCommand, attachment);
    }

    private void ExecuteMessageCommand(object? sender, ICommand? command)
    {
        if (sender is Button { BindingContext: MessageItem message }) Execute(command, message);
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

}
