using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml.Input;
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
    private const int MaximumScrollAttemptsPerLayout = 12;
    private ShellViewModel? _viewModel;
    private VisualElement? _messageMenuTrigger;
    private long? _firstVisibleMessageId;
    private double _firstVisibleViewportOffset;
    private long? _pendingPrependAnchorId;
    private string? _pendingPrependAnchorConversationKey;
    private long _pendingPrependAnchorGeneration;
    private double _pendingPrependAnchorOffset;
    private long? _stabilizedAnchorId;
    private string? _stabilizedAnchorConversationKey;
    private long _stabilizedAnchorGeneration;
    private double _stabilizedAnchorOffset;
    private bool _anchorRestoreScheduled;
    private double? _lastReportedBottomDistance;
    private double _lastObservedExtentHeight;
    private double _lastObservedViewportWidth;
    private double _lastObservedViewportHeight;
    private MessageScrollRequest? _activeScrollRequest;
    private bool _scrollAttemptScheduled;
    private bool _finalScrollIssued;
    private int _scrollAttemptCount;
    private bool _scrollRetrySuspended;
    private double _suspendedScrollExtentHeight;
    private double _suspendedScrollViewportHeight;
    private double _suspendedScrollVerticalOffset;
    private WinUiFrameworkElement? _platformLayoutRoot;
    private readonly PointerEventHandler _viewportPointerInputHandler;
    private readonly KeyEventHandler _viewportKeyInputHandler;

    public MessageListView()
    {
        _viewportPointerInputHandler = OnViewportPointerInput;
        _viewportKeyInputHandler = OnViewportKeyInput;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MessageCollection.HandlerChanged += OnMessageCollectionHandlerChanged;
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
        }
        BeginScrollRequest(_viewModel?.PendingMessageScrollRequest);
        EnsurePlatformLayoutHook();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ShellViewModel.PendingMessageScrollRequest))
        {
            BeginScrollRequest(_viewModel?.PendingMessageScrollRequest);
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

        // CollectionView raises Scrolled while ScrollTo/ChangeView is realizing and
        // positioning the requested item. Treating those intermediate positions as
        // user viewport changes can start older-page loading and install a prepend
        // anchor, which then fights the pending jump-to-latest request.
        if (_activeScrollRequest is not null)
        {
            _lastReportedBottomDistance = GetBottomDistanceDip();
            ScheduleMessageScroll();
            return;
        }

        long? visibleMessageId = null;
        var hasVisibleOffset = false;
        var visibleOffset = 0d;
        if (eventArgs.FirstVisibleItemIndex >= 0 && eventArgs.FirstVisibleItemIndex < _viewModel.Messages.Count)
        {
            visibleMessageId = _viewModel.Messages[eventArgs.FirstVisibleItemIndex].MessageId;
            hasVisibleOffset = TryGetItemViewportOffset(eventArgs.FirstVisibleItemIndex, out visibleOffset);
        }

        if (_pendingPrependAnchorId is { } anchorId)
        {
            if (!IsPendingPrependAnchorCurrent())
            {
                ClearPendingPrependAnchor();
            }
            else if (visibleMessageId == anchorId &&
                     hasVisibleOffset &&
                     Math.Abs(visibleOffset - _pendingPrependAnchorOffset) <= 2d)
            {
                PromotePendingPrependAnchor();
            }
            else
            {
                RestorePrependAnchor(anchorId);
            }
        }
        else
        {
            _firstVisibleMessageId = visibleMessageId;
            _firstVisibleViewportOffset = hasVisibleOffset ? visibleOffset : 0d;
        }

        var bottomDistance = GetBottomDistanceDip();
        _lastReportedBottomDistance = bottomDistance;
        await _viewModel.ReportMessageViewportAsync(
            eventArgs.FirstVisibleItemIndex,
            eventArgs.LastVisibleItemIndex,
            eventArgs.VerticalOffset,
            bottomDistanceDip: bottomDistance);
    }

    private double? GetBottomDistanceDip()
    {
        if (MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return null;
        }

        return Math.Max(0d, scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset);
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_activeScrollRequest is not null)
        {
            ResetScrollRetryBudget();
            ScheduleMessageScroll();
            return;
        }

        if (_firstVisibleMessageId is null || _pendingPrependAnchorId is not null)
        {
            return;
        }

        var currentFirstIndex = FindMessageIndex(_firstVisibleMessageId.Value);
        if (currentFirstIndex < 0) return;
        var affectsAnchor = eventArgs.Action switch
        {
            NotifyCollectionChangedAction.Add => eventArgs.NewStartingIndex >= 0 && currentFirstIndex > eventArgs.NewStartingIndex,
            NotifyCollectionChangedAction.Replace => eventArgs.OldStartingIndex >= 0 && eventArgs.OldStartingIndex <= currentFirstIndex,
            NotifyCollectionChangedAction.Move =>
                eventArgs.OldStartingIndex >= 0 &&
                Math.Min(eventArgs.OldStartingIndex, eventArgs.NewStartingIndex) <= currentFirstIndex,
            NotifyCollectionChangedAction.Remove => eventArgs.OldStartingIndex >= 0 && eventArgs.OldStartingIndex <= currentFirstIndex,
            _ => false
        };
        if (affectsAnchor)
        {
            _pendingPrependAnchorId = _firstVisibleMessageId;
            _pendingPrependAnchorConversationKey = _viewModel?.CurrentConversationKey;
            _pendingPrependAnchorGeneration = _viewModel?.CurrentHistoryGeneration ?? 0;
            _pendingPrependAnchorOffset = _firstVisibleViewportOffset;
            ClearStabilizedAnchor();
        }
    }

    private void RestorePrependAnchor(long messageId)
    {
        var index = FindMessageIndex(messageId);
        if (index < 0 ||
            !IsPendingPrependAnchorCurrent() ||
            _activeScrollRequest is not null ||
            _anchorRestoreScheduled)
        {
            return;
        }
        _anchorRestoreScheduled = true;
        var desiredOffset = _pendingPrependAnchorOffset;
        var conversationKey = _pendingPrependAnchorConversationKey;
        var generation = _pendingPrependAnchorGeneration;
        MessageCollection.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
        Dispatcher.Dispatch(() =>
        {
            RestoreNativeViewportOffset(index, desiredOffset, conversationKey, generation);
            _anchorRestoreScheduled = false;
        });
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

    private void RestoreNativeViewportOffset(
        int index,
        double desiredOffset,
        string? conversationKey,
        long generation)
    {
        if (_activeScrollRequest is not null ||
            _viewModel is null ||
            !string.Equals(_viewModel.CurrentConversationKey, conversationKey, StringComparison.Ordinal) ||
            _viewModel.CurrentHistoryGeneration != generation)
        {
            return;
        }
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

    private bool IsPendingPrependAnchorCurrent() =>
        _viewModel is not null &&
        string.Equals(
            _viewModel.CurrentConversationKey,
            _pendingPrependAnchorConversationKey,
            StringComparison.Ordinal) &&
        _viewModel.CurrentHistoryGeneration == _pendingPrependAnchorGeneration;

    private void PromotePendingPrependAnchor()
    {
        _stabilizedAnchorId = _pendingPrependAnchorId;
        _stabilizedAnchorConversationKey = _pendingPrependAnchorConversationKey;
        _stabilizedAnchorGeneration = _pendingPrependAnchorGeneration;
        _stabilizedAnchorOffset = _pendingPrependAnchorOffset;
        _firstVisibleMessageId = _pendingPrependAnchorId;
        _firstVisibleViewportOffset = _pendingPrependAnchorOffset;
        ClearPendingPrependAnchor();
    }

    private void ClearPendingPrependAnchor()
    {
        _pendingPrependAnchorId = null;
        _pendingPrependAnchorConversationKey = null;
        _pendingPrependAnchorGeneration = 0;
        _pendingPrependAnchorOffset = 0d;
        _anchorRestoreScheduled = false;
    }

    private bool IsStabilizedAnchorCurrent() =>
        _viewModel is not null &&
        string.Equals(
            _viewModel.CurrentConversationKey,
            _stabilizedAnchorConversationKey,
            StringComparison.Ordinal) &&
        _viewModel.CurrentHistoryGeneration == _stabilizedAnchorGeneration;

    private void ClearStabilizedAnchor()
    {
        _stabilizedAnchorId = null;
        _stabilizedAnchorConversationKey = null;
        _stabilizedAnchorGeneration = 0;
        _stabilizedAnchorOffset = 0d;
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

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        EnsurePlatformLayoutHook();
        BeginScrollRequest(_viewModel?.PendingMessageScrollRequest);
    }

    private void OnUnloaded(object? sender, EventArgs eventArgs)
    {
        RemovePlatformLayoutHook();
        ClearActiveScrollRequest();
        ClearPendingPrependAnchor();
        ClearStabilizedAnchor();
    }

    private void OnMessageCollectionHandlerChanged(object? sender, EventArgs eventArgs)
    {
        EnsurePlatformLayoutHook();
        ResetScrollRetryBudget();
        ScheduleMessageScroll();
    }

    private void EnsurePlatformLayoutHook()
    {
        var root = MessageCollection.Handler?.PlatformView as WinUiFrameworkElement;
        if (ReferenceEquals(root, _platformLayoutRoot)) return;
        RemovePlatformLayoutHook();
        _platformLayoutRoot = root;
        if (_platformLayoutRoot is not null)
        {
            _platformLayoutRoot.LayoutUpdated += OnPlatformLayoutUpdated;
            _platformLayoutRoot.AddHandler(
                Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                _viewportPointerInputHandler,
                true);
            _platformLayoutRoot.AddHandler(
                Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent,
                _viewportPointerInputHandler,
                true);
            _platformLayoutRoot.AddHandler(
                Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                _viewportKeyInputHandler,
                true);
        }
    }

    private void RemovePlatformLayoutHook()
    {
        if (_platformLayoutRoot is not null)
        {
            _platformLayoutRoot.RemoveHandler(
                Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                _viewportKeyInputHandler);
            _platformLayoutRoot.RemoveHandler(
                Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent,
                _viewportPointerInputHandler);
            _platformLayoutRoot.RemoveHandler(
                Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
                _viewportPointerInputHandler);
            _platformLayoutRoot.LayoutUpdated -= OnPlatformLayoutUpdated;
        }
        _platformLayoutRoot = null;
    }

    private void OnViewportPointerInput(object sender, PointerRoutedEventArgs eventArgs) => ClearViewportAnchorsForUserInput();

    private void OnViewportKeyInput(object sender, KeyRoutedEventArgs eventArgs)
    {
        if (eventArgs.Key is Windows.System.VirtualKey.Up or
            Windows.System.VirtualKey.Down or
            Windows.System.VirtualKey.Left or
            Windows.System.VirtualKey.Right or
            Windows.System.VirtualKey.PageUp or
            Windows.System.VirtualKey.PageDown or
            Windows.System.VirtualKey.Home or
            Windows.System.VirtualKey.End or
            Windows.System.VirtualKey.Space)
        {
            ClearViewportAnchorsForUserInput();
        }
    }

    private void ClearViewportAnchorsForUserInput()
    {
        ClearPendingPrependAnchor();
        ClearStabilizedAnchor();
    }

    private void OnPlatformLayoutUpdated(object? sender, object eventArgs)
    {
        if (_activeScrollRequest is not null)
        {
            if (_scrollRetrySuspended && !ResumeScrollRetriesAfterLayoutChange()) return;
            ScheduleMessageScroll();
            return;
        }

        MaintainBottomAfterLayoutMetricsChange();
        ReportLayoutBottomDistance();
        MaintainViewportAnchorAfterLayout();
    }

    private void MaintainBottomAfterLayoutMetricsChange()
    {
        if (_viewModel is null ||
            MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return;
        }

        var hadMetrics = _lastObservedExtentHeight > 0d &&
            _lastObservedViewportWidth > 0d &&
            _lastObservedViewportHeight > 0d;
        var metricsChanged = hadMetrics &&
            (Math.Abs(scrollViewer.ExtentHeight - _lastObservedExtentHeight) > 0.5d ||
             Math.Abs(scrollViewer.ViewportWidth - _lastObservedViewportWidth) > 0.5d ||
             Math.Abs(scrollViewer.ViewportHeight - _lastObservedViewportHeight) > 0.5d);
        var wasNearBottom = _lastReportedBottomDistance is <= MessageViewportPolicy.NearBottomDistanceDip;

        _lastObservedExtentHeight = scrollViewer.ExtentHeight;
        _lastObservedViewportWidth = scrollViewer.ViewportWidth;
        _lastObservedViewportHeight = scrollViewer.ViewportHeight;
        if (!metricsChanged || !wasNearBottom) return;

        var bottomDistance = Math.Max(0d, scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset);
        if (bottomDistance <= 2d) return;
        scrollViewer.ChangeView(
            null,
            scrollViewer.ScrollableHeight,
            null,
            disableAnimation: true);
    }

    private void ReportLayoutBottomDistance()
    {
        if (_viewModel is null || GetBottomDistanceDip() is not { } distance) return;
        if (_lastReportedBottomDistance is { } previous && Math.Abs(previous - distance) <= 0.5d) return;
        _lastReportedBottomDistance = distance;
        _viewModel.ReportMessageBottomDistance(distance);
    }

    private void MaintainViewportAnchorAfterLayout()
    {
        if (_pendingPrependAnchorId is { } pendingId)
        {
            if (!IsPendingPrependAnchorCurrent())
            {
                ClearPendingPrependAnchor();
                return;
            }
            var pendingIndex = FindMessageIndex(pendingId);
            if (pendingIndex >= 0 &&
                TryGetItemViewportOffset(pendingIndex, out var pendingOffset) &&
                Math.Abs(pendingOffset - _pendingPrependAnchorOffset) <= 2d)
            {
                PromotePendingPrependAnchor();
                return;
            }
            RestorePrependAnchor(pendingId);
            return;
        }

        if (_stabilizedAnchorId is not { } stabilizedId) return;
        if (!IsStabilizedAnchorCurrent())
        {
            ClearStabilizedAnchor();
            return;
        }
        if (_firstVisibleMessageId != stabilizedId || _anchorRestoreScheduled) return;
        var index = FindMessageIndex(stabilizedId);
        if (index < 0 ||
            !TryGetItemViewportOffset(index, out var currentOffset) ||
            Math.Abs(currentOffset - _stabilizedAnchorOffset) <= 2d)
        {
            return;
        }

        _anchorRestoreScheduled = true;
        Dispatcher.Dispatch(() =>
        {
            RestoreNativeViewportOffset(
                index,
                _stabilizedAnchorOffset,
                _stabilizedAnchorConversationKey,
                _stabilizedAnchorGeneration);
            _anchorRestoreScheduled = false;
        });
    }

    private void BeginScrollRequest(MessageScrollRequest? request)
    {
        if (request is not null && _activeScrollRequest?.Sequence == request.Sequence)
        {
            ScheduleMessageScroll();
            return;
        }

        ClearActiveScrollRequest();
        if (request is null) return;
        ClearPendingPrependAnchor();
        ClearStabilizedAnchor();
        _firstVisibleMessageId = null;
        _firstVisibleViewportOffset = 0d;
        _activeScrollRequest = request;
        ResetScrollRetryBudget();
        ScheduleMessageScroll();
    }

    private void ClearActiveScrollRequest()
    {
        _activeScrollRequest = null;
        _scrollAttemptScheduled = false;
        _finalScrollIssued = false;
        ResetScrollRetryBudget();
    }

    private void ScheduleMessageScroll()
    {
        if (_activeScrollRequest is null || _scrollAttemptScheduled || !IsLoaded) return;
        _scrollAttemptScheduled = true;
        Dispatcher.Dispatch(TryProcessMessageScroll);
    }

    private void TryProcessMessageScroll()
    {
        _scrollAttemptScheduled = false;
        var request = _activeScrollRequest;
        if (request is null || _viewModel is null) return;
        if (!_viewModel.IsMessageScrollRequestCurrent(request))
        {
            BeginScrollRequest(_viewModel.PendingMessageScrollRequest);
            return;
        }

        var index = FindMessageIndex(request.TargetMessageId);
        if (index < 0) return;
        if (MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            (platformRoot as WinUiFrameworkElement)?.IsLoaded != true)
        {
            return;
        }

        var list = platformRoot as WinUiListViewBase ?? FindDescendant<WinUiListViewBase>(platformRoot);
        var scrollViewer = FindDescendant<WinUiScrollViewer>(platformRoot);
        if (list is null ||
            scrollViewer is null ||
            scrollViewer.ExtentHeight <= 0d ||
            scrollViewer.ViewportHeight <= 0d)
        {
            return;
        }

        if (list.ContainerFromIndex(index) is not WinUiFrameworkElement { IsLoaded: true, ActualHeight: > 0d } container)
        {
            if (_scrollAttemptCount >= MaximumScrollAttemptsPerLayout)
            {
                SuspendScrollRetries(scrollViewer);
                return;
            }
            _finalScrollIssued = false;
            _scrollAttemptCount++;
            MessageCollection.ScrollTo(index, position: ScrollToPosition.Center, animate: false);
            return;
        }

        if (IsScrollRequestSatisfied(container, scrollViewer))
        {
            _lastObservedExtentHeight = scrollViewer.ExtentHeight;
            _lastObservedViewportWidth = scrollViewer.ViewportWidth;
            _lastObservedViewportHeight = scrollViewer.ViewportHeight;
            _viewModel.AcknowledgeMessageScrollRequest(request);
            ClearActiveScrollRequest();
            return;
        }

        if (!_finalScrollIssued)
        {
            _finalScrollIssued = true;
            _scrollAttemptCount = 1;
            scrollViewer.ChangeView(
                null,
                scrollViewer.ScrollableHeight,
                null,
                disableAnimation: true);
            ScheduleMessageScroll();
            return;
        }

        if (_scrollAttemptCount >= MaximumScrollAttemptsPerLayout)
        {
            SuspendScrollRetries(scrollViewer);
            return;
        }

        _scrollAttemptCount++;
        scrollViewer.ChangeView(
            null,
            scrollViewer.ScrollableHeight,
            null,
            disableAnimation: true);
        ScheduleMessageScroll();
    }

    private void ResetScrollRetryBudget()
    {
        _scrollAttemptCount = 0;
        _scrollRetrySuspended = false;
        _suspendedScrollExtentHeight = 0d;
        _suspendedScrollViewportHeight = 0d;
        _suspendedScrollVerticalOffset = 0d;
    }

    private void SuspendScrollRetries(WinUiScrollViewer scrollViewer)
    {
        _scrollRetrySuspended = true;
        _suspendedScrollExtentHeight = scrollViewer.ExtentHeight;
        _suspendedScrollViewportHeight = scrollViewer.ViewportHeight;
        _suspendedScrollVerticalOffset = scrollViewer.VerticalOffset;
    }

    private bool ResumeScrollRetriesAfterLayoutChange()
    {
        if (MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return false;
        }

        var layoutChanged = Math.Abs(scrollViewer.ExtentHeight - _suspendedScrollExtentHeight) > 0.5d ||
            Math.Abs(scrollViewer.ViewportHeight - _suspendedScrollViewportHeight) > 0.5d ||
            Math.Abs(scrollViewer.VerticalOffset - _suspendedScrollVerticalOffset) > 0.5d;
        if (!layoutChanged) return false;
        ResetScrollRetryBudget();
        return true;
    }

    private static bool IsScrollRequestSatisfied(
        WinUiFrameworkElement container,
        WinUiScrollViewer scrollViewer)
    {
        try
        {
            var targetTop = container.TransformToVisual(scrollViewer).TransformPoint(new WinPoint(0, 0)).Y;
            var targetBottom = targetTop + container.ActualHeight;
            var bottomDistance = Math.Max(0d, scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset);
            return bottomDistance <= 2d &&
                targetBottom >= -2d &&
                targetTop <= scrollViewer.ViewportHeight + 2d;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
