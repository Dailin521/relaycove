using System.ComponentModel;
using System.Windows.Input;
using RelayCove.App.ViewModels;
using WinPoint = Windows.Foundation.Point;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;

namespace RelayCove.App.Controls;

public partial class MessageListView : ContentView
{
    private ShellViewModel? _viewModel;
    private VisualElement? _messageMenuTrigger;

    public MessageListView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnBindingContextChanged();
        _viewModel = BindingContext as ShellViewModel;
        OnPropertyChanged(nameof(ViewModel));
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
        if (sender is not VisualElement { BindingContext: MessageItem message } trigger) return;
        _messageMenuTrigger = trigger;
        var request = CreateMenuRequest(trigger, message);
        if (request is null) Execute(_viewModel?.OpenMessageMenuCommand, message);
        else Execute(_viewModel?.OpenMessageMenuAtCommand, request);
    }

    private static MessageMenuRequest? CreateMenuRequest(VisualElement trigger, MessageItem message)
    {
        var source = trigger.Handler?.PlatformView as WinUiFrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<WinUiFrameworkElement>()
            .FirstOrDefault();
        if (source is null || pageRoot is null) return null;
        try
        {
            var localX = message.IsOwn ? 0d : source.ActualWidth;
            var point = source.TransformToVisual(pageRoot)
                .TransformPoint(new WinPoint(localX, source.ActualHeight));
            return new MessageMenuRequest(message, point.X, point.Y);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnQuoteMessageClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.QuoteMessageCommand);

    private void OnCopyMessageClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.CopyMessageRawCommand);

    private void OnOpenReactionPickerClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.OpenReactionPickerCommand);

    private void OnEditMessageClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.OpenEditDialogCommand);

    private void OnToggleMessageStarClicked(object? sender, EventArgs eventArgs) =>
        ExecuteMessageCommand(sender, _viewModel?.ToggleMessageStarCommand);

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
        if (sender is VisualElement { BindingContext: MessageItem message }) Execute(command, message);
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

}
