using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using RelayCove.App.ViewModels;
using WinPoint = Windows.Foundation.Point;
using WinUiDependencyObject = Microsoft.UI.Xaml.DependencyObject;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;
using WinUiListViewBase = Microsoft.UI.Xaml.Controls.ListViewBase;
using WinUiScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;
using WinUiVisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;

namespace RelayCove.App.Controls;

public partial class MessageListView : ContentView
{
    private ShellViewModel? _viewModel;
    private VisualElement? _messageMenuTrigger;
    private long? _firstVisibleMessageId;
    private double _firstVisibleViewportOffset;
    private long? _pendingPrependAnchorId;
    private int _lastScrollToLatestRequest;

    public MessageListView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        }
        base.OnBindingContextChanged();
        _viewModel = BindingContext as ShellViewModel;
        OnPropertyChanged(nameof(ViewModel));
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            _lastScrollToLatestRequest = _viewModel.ScrollToLatestRequest;
            if (_viewModel.Messages.Count > 0) Dispatcher.Dispatch(ScrollToLatest);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ShellViewModel.ScrollToLatestRequest) &&
            _viewModel is not null &&
            _lastScrollToLatestRequest != _viewModel.ScrollToLatestRequest)
        {
            _lastScrollToLatestRequest = _viewModel.ScrollToLatestRequest;
            Dispatcher.Dispatch(ScrollToLatest);
            return;
        }

        if (eventArgs.PropertyName == nameof(ShellViewModel.MessageActionFocusRequest) &&
            _messageMenuTrigger is not null)
        {
            Dispatcher.Dispatch(() =>
            {
                _messageMenuTrigger?.Focus();
                _messageMenuTrigger = null;
            });
        }
    }

    private async void OnMessageCollectionScrolled(object? sender, ItemsViewScrolledEventArgs eventArgs)
    {
        if (_viewModel is null) return;
        if (eventArgs.FirstVisibleItemIndex >= 0 && eventArgs.FirstVisibleItemIndex < _viewModel.Messages.Count)
        {
            _firstVisibleMessageId = _viewModel.Messages[eventArgs.FirstVisibleItemIndex].MessageId;
            _firstVisibleViewportOffset = TryGetItemViewportOffset(eventArgs.FirstVisibleItemIndex, out var offset)
                ? offset
                : 0d;
        }

        if (_pendingPrependAnchorId is { } anchorId)
        {
            var visibleMessageId = eventArgs.FirstVisibleItemIndex >= 0 &&
                eventArgs.FirstVisibleItemIndex < _viewModel.Messages.Count
                    ? _viewModel.Messages[eventArgs.FirstVisibleItemIndex].MessageId
                    : null;
            if (visibleMessageId == anchorId)
            {
                _pendingPrependAnchorId = null;
            }
            else
            {
                RestorePrependAnchor(anchorId);
            }
        }

        await _viewModel.ReportMessageViewportAsync(
            eventArgs.FirstVisibleItemIndex,
            eventArgs.LastVisibleItemIndex,
            eventArgs.VerticalOffset);
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_firstVisibleMessageId is null ||
            eventArgs.Action != NotifyCollectionChangedAction.Add ||
            eventArgs.NewStartingIndex < 0)
        {
            return;
        }

        var currentFirstIndex = FindMessageIndex(_firstVisibleMessageId.Value);
        if (currentFirstIndex > eventArgs.NewStartingIndex)
        {
            _pendingPrependAnchorId = _firstVisibleMessageId;
        }
    }

    private void RestorePrependAnchor(long messageId)
    {
        var index = FindMessageIndex(messageId);
        if (index < 0) return;
        var desiredOffset = _firstVisibleViewportOffset;
        _pendingPrependAnchorId = null;
        MessageCollection.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
        Dispatcher.Dispatch(() => RestoreNativeViewportOffset(index, desiredOffset));
    }

    private int FindMessageIndex(long messageId)
    {
        if (_viewModel is null) return -1;
        for (var index = 0; index < _viewModel.Messages.Count; index++)
        {
            if (_viewModel.Messages[index].MessageId == messageId) return index;
        }
        return -1;
    }

    private bool TryGetItemViewportOffset(int index, out double offset)
    {
        offset = 0d;
        if (MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot) return false;
        var list = platformRoot as WinUiListViewBase ?? FindDescendant<WinUiListViewBase>(platformRoot);
        var scrollViewer = FindDescendant<WinUiScrollViewer>(platformRoot);
        if (list?.ContainerFromIndex(index) is not WinUiFrameworkElement container || scrollViewer is null) return false;
        try
        {
            offset = container.TransformToVisual(scrollViewer).TransformPoint(new WinPoint(0, 0)).Y;
            return double.IsFinite(offset);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void RestoreNativeViewportOffset(int index, double desiredOffset)
    {
        if (!TryGetItemViewportOffset(index, out var currentOffset) ||
            MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return;
        }

        var correction = currentOffset - desiredOffset;
        if (Math.Abs(correction) <= 0.5d) return;
        scrollViewer.ChangeView(
            null,
            Math.Max(0d, scrollViewer.VerticalOffset + correction),
            null,
            disableAnimation: true);
    }

    private static T? FindDescendant<T>(WinUiDependencyObject root) where T : WinUiDependencyObject
    {
        var count = WinUiVisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = WinUiVisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ScrollToLatest()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0) return;
        MessageCollection.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
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
