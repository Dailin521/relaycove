using System.Collections.ObjectModel;
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
using WinUiTransitionCollection = Microsoft.UI.Xaml.Media.Animation.TransitionCollection;
using WinUiTranslateTransform = Microsoft.UI.Xaml.Media.TranslateTransform;
using WinUiVisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;

namespace RelayCove.App.Controls;

public partial class MessageListView : ContentView
{
    private const double ReactionPickerWidth = 310d;
    private const double ReactionPickerEdgeMargin = 12d;
    private const double MessageInsertionInitialOpacity = 0d;
    private const uint MessageInsertionDelay = 100;
    private const uint MessageInsertionDuration = 140;
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
    private double? _lastReportedViewportHeight;
    private string? _viewportConversationKey;
    private long _viewportHistoryGeneration;
    private MessageScrollRequest? _activeScrollRequest;
    private bool _scrollAttemptScheduled;
    private bool _finalScrollIssued;
    private int _scrollAttemptCount;
    private bool _scrollRetrySuspended;
    private double _suspendedScrollExtentHeight;
    private double _suspendedScrollViewportHeight;
    private double _suspendedScrollVerticalOffset;
    private bool _keepLatestMessageInView;
    private WinUiFrameworkElement? _platformLayoutRoot;
    private WinUiListViewBase? _platformMessageList;
    private readonly HashSet<string> _pendingInsertionAnimationIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedInsertionAnimationIds = new(StringComparer.Ordinal);
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

    public static readonly BindableProperty MessageItemsProperty = BindableProperty.Create(
        nameof(MessageItems),
        typeof(ObservableCollection<MessageItem>),
        typeof(MessageListView),
        propertyChanged: OnMessageItemsChanged);

    public static readonly BindableProperty ConversationKeyProperty = BindableProperty.Create(
        nameof(ConversationKey),
        typeof(string),
        typeof(MessageListView));

    public ObservableCollection<MessageItem>? MessageItems
    {
        get => (ObservableCollection<MessageItem>?)GetValue(MessageItemsProperty);
        set => SetValue(MessageItemsProperty, value);
    }

    public string? ConversationKey
    {
        get => (string?)GetValue(ConversationKeyProperty);
        set => SetValue(ConversationKeyProperty, value);
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        base.OnBindingContextChanged();
        _viewModel = BindingContext as ShellViewModel;
        OnPropertyChanged(nameof(ViewModel));
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        BeginScrollRequest(CurrentScrollRequest());
        EnsurePlatformLayoutHook();
    }

    private static void OnMessageItemsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (MessageListView)bindable;
        if (oldValue is ObservableCollection<MessageItem> previous)
            previous.CollectionChanged -= view.OnMessagesCollectionChanged;
        if (newValue is ObservableCollection<MessageItem> current)
            current.CollectionChanged += view.OnMessagesCollectionChanged;
        view.BeginScrollRequest(view.CurrentScrollRequest());
    }

    private MessageScrollRequest? CurrentScrollRequest()
    {
        var request = _viewModel?.PendingMessageScrollRequest;
        return request is not null &&
               string.Equals(request.ConversationKey, ConversationKey, StringComparison.Ordinal)
            ? request
            : null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ShellViewModel.PendingMessageScrollRequest))
        {
            BeginScrollRequest(CurrentScrollRequest());
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
        if (_viewModel is null || MessageItems is null ||
            !string.Equals(_viewModel.CurrentConversationKey, ConversationKey, StringComparison.Ordinal)) return;

        // CollectionView raises Scrolled while ScrollTo/ChangeView is realizing and
        // positioning the requested item. Treating those intermediate positions as
        // user viewport changes can start older-page loading and install a prepend
        // anchor, which then fights the pending jump-to-latest request.
        if (_activeScrollRequest is not null)
        {
            if (TryGetViewportMetrics(out var activeBottomDistance, out var activeViewportHeight))
            {
                _lastReportedBottomDistance = activeBottomDistance;
                _lastReportedViewportHeight = activeViewportHeight;
            }
            ScheduleMessageScroll();
            return;
        }

        long? visibleMessageId = null;
        var hasVisibleOffset = false;
        var visibleOffset = 0d;
        if (eventArgs.FirstVisibleItemIndex >= 0 && eventArgs.FirstVisibleItemIndex < MessageItems.Count)
        {
            visibleMessageId = MessageItems[eventArgs.FirstVisibleItemIndex].MessageId;
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

        var hasViewportMetrics = TryGetViewportMetrics(out var bottomDistance, out var viewportHeight);
        _lastReportedBottomDistance = hasViewportMetrics ? bottomDistance : null;
        _lastReportedViewportHeight = hasViewportMetrics ? viewportHeight : null;
        await _viewModel.ReportMessageViewportAsync(
            eventArgs.FirstVisibleItemIndex,
            eventArgs.LastVisibleItemIndex,
            eventArgs.VerticalOffset,
            bottomDistanceDip: hasViewportMetrics ? bottomDistance : null,
            viewportHeightDip: hasViewportMetrics ? viewportHeight : null,
            expectedConversationKey: _viewportConversationKey,
            expectedHistoryGeneration: _viewportHistoryGeneration);
    }

    private double? GetBottomDistanceDip()
    {
        return TryGetViewportMetrics(out var bottomDistance, out _)
            ? bottomDistance
            : null;
    }

    private bool TryGetViewportMetrics(out double bottomDistanceDip, out double viewportHeightDip)
    {
        bottomDistanceDip = 0d;
        viewportHeightDip = 0d;
        if (MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return false;
        }

        bottomDistanceDip = Math.Max(0d, scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset);
        viewportHeightDip = Math.Max(0d, scrollViewer.ViewportHeight);
        return true;
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Add &&
            eventArgs.NewItems?.OfType<MessageItem>()
                .Where(item => item.IsInsertionAnimationPending)
                .ToArray() is { Length: > 0 } insertedItems)
        {
            foreach (var item in insertedItems) _pendingInsertionAnimationIds.Add(item.Id);
            _keepLatestMessageInView = true;
        }

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
        if (MessageItems is null) return -1;
        for (var index = 0; index < MessageItems.Count; index++)
        {
            if (MessageItems[index].MessageId == messageId) return index;
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
        BeginScrollRequest(CurrentScrollRequest());
    }

    private void OnUnloaded(object? sender, EventArgs eventArgs)
    {
        RemovePlatformLayoutHook();
        _keepLatestMessageInView = false;
        _pendingInsertionAnimationIds.Clear();
        _preparedInsertionAnimationIds.Clear();
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
        if (ReferenceEquals(root, _platformLayoutRoot))
        {
            EnsurePlatformMessageListConfigured();
            return;
        }
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
            EnsurePlatformMessageListConfigured();
        }
    }

    private void EnsurePlatformMessageListConfigured()
    {
        if (_platformMessageList is not null || _platformLayoutRoot is null) return;
        var list = _platformLayoutRoot as WinUiListViewBase ??
            FindDescendant<WinUiListViewBase>(_platformLayoutRoot);
        if (list is null) return;

        // WinUI's default add transition keeps a newly inserted virtualized row
        // transparent before its reveal starts. RelayCove owns the immediate
        // compositor fade, so the platform transition must not run as well.
        list.ItemContainerTransitions = new WinUiTransitionCollection();
        _platformMessageList = list;
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
        _platformMessageList = null;
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
        _keepLatestMessageInView = false;
        ClearPendingPrependAnchor();
        ClearStabilizedAnchor();
    }

    private void OnPlatformLayoutUpdated(object? sender, object eventArgs)
    {
        EnsurePlatformMessageListConfigured();
        if (_activeScrollRequest is not null)
        {
            if (_scrollRetrySuspended && !ResumeScrollRetriesAfterLayoutChange()) return;
            ScheduleMessageScroll();
            return;
        }

        MaintainLatestMessageAfterLayout();
        StartVisibleInsertionAnimations();
        ReportLayoutBottomDistance();
        MaintainViewportAnchorAfterLayout();
    }

    private void MaintainLatestMessageAfterLayout()
    {
        if (!_keepLatestMessageInView ||
            MessageCollection.Handler?.PlatformView is not WinUiDependencyObject platformRoot ||
            FindDescendant<WinUiScrollViewer>(platformRoot) is not { } scrollViewer)
        {
            return;
        }

        if (!MessageViewportPolicy.ShouldMaintainLatest(
                _keepLatestMessageInView,
                scrollViewer.ScrollableHeight,
                scrollViewer.VerticalOffset))
        {
            return;
        }
        scrollViewer.ChangeView(
            null,
            scrollViewer.ScrollableHeight,
            null,
            disableAnimation: true);
    }

    private void ReportLayoutBottomDistance()
    {
        if (_viewModel is null || !TryGetViewportMetrics(out var distance, out var viewportHeight)) return;
        if (_lastReportedBottomDistance is { } previousDistance &&
            _lastReportedViewportHeight is { } previousViewportHeight &&
            Math.Abs(previousDistance - distance) <= 0.5d &&
            Math.Abs(previousViewportHeight - viewportHeight) <= 0.5d)
        {
            return;
        }
        _lastReportedBottomDistance = distance;
        _lastReportedViewportHeight = viewportHeight;
        _viewModel.ReportMessageBottomDistance(
            distance,
            viewportHeight,
            _viewportConversationKey,
            _viewportHistoryGeneration);
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
        if (request is null)
        {
            if (_viewModel is null ||
                !string.Equals(_viewportConversationKey, _viewModel.CurrentConversationKey, StringComparison.Ordinal) ||
                _viewportHistoryGeneration != _viewModel.CurrentHistoryGeneration)
            {
                _viewportConversationKey = null;
                _viewportHistoryGeneration = 0;
            }
            return;
        }
        ClearPendingPrependAnchor();
        ClearStabilizedAnchor();
        _firstVisibleMessageId = null;
        _firstVisibleViewportOffset = 0d;
        _viewportConversationKey = request.ConversationKey;
        _viewportHistoryGeneration = request.Generation;
        _activeScrollRequest = request;
        _keepLatestMessageInView = ShouldKeepLatestMessageInView(request);
        SetActivationPositioning(IsActivationRequest(request));
        ResetScrollRetryBudget();
        ScheduleMessageScroll();
    }

    private void ClearActiveScrollRequest()
    {
        _activeScrollRequest = null;
        _scrollAttemptScheduled = false;
        _finalScrollIssued = false;
        SetActivationPositioning(false);
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

        var index = FindMessageIndex(request.TargetMessageId);
        if (index < 0) return;

        if (request.Reason == MessageScrollReason.MessageAnchor)
        {
            if (list.ContainerFromIndex(index) is WinUiFrameworkElement { IsLoaded: true, ActualHeight: > 0d } anchorContainer &&
                IsTargetVisible(anchorContainer, scrollViewer))
            {
                _viewModel.AcknowledgeMessageScrollRequest(request);
                ClearActiveScrollRequest();
                return;
            }

            if (_scrollAttemptCount >= MaximumScrollAttemptsPerLayout)
            {
                SuspendScrollRetries(scrollViewer);
                return;
            }

            _scrollAttemptCount++;
            MessageCollection.ScrollTo(index, position: ScrollToPosition.Center, animate: false);
            return;
        }

        if (request.Reason is MessageScrollReason.RealtimeFollow or
            MessageScrollReason.ConversationActivated or
            MessageScrollReason.ConversationReactivated)
        {
            // Do not acknowledge against the old empty viewport. The target
            // must exist in WinUI before the native bottom position is valid.
            if (list.ContainerFromIndex(index) is not WinUiFrameworkElement { IsLoaded: true, ActualHeight: > 0d } targetContainer)
            {
                if (MessageViewportPolicy.ShouldUseNativeOffsetBeforeTargetRealized(request.Reason))
                {
                    if (!MessageViewportPolicy.ShouldIssueLatestScroll(request.Reason, _finalScrollIssued) ||
                        !MessageViewportPolicy.ShouldMaintainLatest(
                            isBottomPinned: true,
                            scrollViewer.ScrollableHeight,
                            scrollViewer.VerticalOffset))
                    {
                        return;
                    }

                    _scrollAttemptCount++;
                    _finalScrollIssued = true;
                    scrollViewer.ChangeView(
                        null,
                        scrollViewer.ScrollableHeight,
                        null,
                        disableAnimation: !MessageViewportPolicy.ShouldAnimateLatestScroll(request.Reason));
                    return;
                }

                if (!MessageViewportPolicy.ShouldIssueLatestScroll(request.Reason, _finalScrollIssued))
                {
                    return;
                }
                if (_scrollAttemptCount >= MaximumScrollAttemptsPerLayout)
                {
                    SuspendScrollRetries(scrollViewer);
                    return;
                }
                _scrollAttemptCount++;
                _finalScrollIssued = MessageViewportPolicy.ShouldAnimateLatestScroll(request.Reason);
                MessageCollection.ScrollTo(
                    index,
                    position: ScrollToPosition.End,
                    animate: MessageViewportPolicy.ShouldAnimateLatestScroll(request.Reason));
                return;
            }

            if (IsScrollRequestSatisfied(targetContainer, scrollViewer))
            {
                _viewModel.AcknowledgeMessageScrollRequest(request);
                ClearActiveScrollRequest();
                return;
            }

            if (_scrollAttemptCount >= MaximumScrollAttemptsPerLayout)
            {
                SuspendScrollRetries(scrollViewer);
                return;
            }

            if (!MessageViewportPolicy.ShouldIssueLatestScroll(request.Reason, _finalScrollIssued))
            {
                return;
            }

            _scrollAttemptCount++;
            _finalScrollIssued = MessageViewportPolicy.ShouldAnimateLatestScroll(request.Reason);
            scrollViewer.ChangeView(
                null,
                scrollViewer.ScrollableHeight,
                null,
                disableAnimation: !MessageViewportPolicy.ShouldAnimateLatestScroll(request.Reason));
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

    private static bool IsActivationRequest(MessageScrollRequest request) =>
        request.Reason is MessageScrollReason.ConversationActivated or MessageScrollReason.ConversationReactivated;

    private static bool ShouldKeepLatestMessageInView(MessageScrollRequest request) =>
        request.Reason is MessageScrollReason.ConversationActivated or
            MessageScrollReason.ConversationReactivated or
            MessageScrollReason.RealtimeFollow or
            MessageScrollReason.ManualJumpToLatest;

    private void SetActivationPositioning(bool isPositioning)
    {
        MessageCollection.InputTransparent = isPositioning;
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

    private void StartVisibleInsertionAnimations()
    {
        if (_pendingInsertionAnimationIds.Count == 0 ||
            _viewModel is null ||
            _platformMessageList is null ||
            _platformLayoutRoot is null ||
            FindDescendant<WinUiScrollViewer>(_platformLayoutRoot) is not { } scrollViewer)
        {
            return;
        }

        foreach (var id in _pendingInsertionAnimationIds.ToArray())
        {
            var index = -1;
            if (MessageItems is null) return;
            for (var candidate = 0; candidate < MessageItems.Count; candidate++)
            {
                if (!string.Equals(MessageItems[candidate].Id, id, StringComparison.Ordinal)) continue;
                index = candidate;
                break;
            }

            if (index < 0)
            {
                _pendingInsertionAnimationIds.Remove(id);
                _preparedInsertionAnimationIds.Remove(id);
                continue;
            }

            if (_platformMessageList.ContainerFromIndex(index) is not WinUiFrameworkElement { IsLoaded: true } container)
            {
                continue;
            }

            if (_preparedInsertionAnimationIds.Add(id)) PrepareInsertionVisual(container);
            if (!IsTargetVisible(container, scrollViewer)) continue;

            var message = MessageItems[index];
            _pendingInsertionAnimationIds.Remove(id);
            _preparedInsertionAnimationIds.Remove(id);
            if (!message.TryConsumeInsertionAnimation()) continue;
            StartInsertionFade(container);
        }
    }

    private static void PrepareInsertionVisual(WinUiFrameworkElement platformElement)
    {
        platformElement.Opacity = MessageInsertionInitialOpacity;
        platformElement.RenderTransform = new WinUiTranslateTransform { Y = 6d };
    }

    private static void StartInsertionFade(WinUiFrameworkElement platformElement)
    {
        var transform = platformElement.RenderTransform as WinUiTranslateTransform ??
            new WinUiTranslateTransform { Y = 6d };
        platformElement.RenderTransform = transform;
        var beginTime = TimeSpan.FromMilliseconds(MessageInsertionDelay);
        var duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(MessageInsertionDuration));
        var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase
        {
            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
        };
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = MessageInsertionInitialOpacity,
            To = 1d,
            BeginTime = beginTime,
            Duration = duration,
            EasingFunction = easing
        };
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 6d,
            To = 0d,
            BeginTime = beginTime,
            Duration = duration,
            EasingFunction = easing
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, platformElement);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, transform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "Y");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) =>
        {
            platformElement.Opacity = 1d;
            transform.Y = 0d;
        };
        storyboard.Begin();
    }

    private static bool IsTargetVisible(
        WinUiFrameworkElement container,
        WinUiScrollViewer scrollViewer)
    {
        try
        {
            var targetTop = container.TransformToVisual(scrollViewer).TransformPoint(new WinPoint(0, 0)).Y;
            var targetBottom = targetTop + container.ActualHeight;
            return targetBottom >= -2d && targetTop <= scrollViewer.ViewportHeight + 2d;
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

    private void OnOpenReactionPickerClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement { BindingContext: MessageItem message } trigger) return;
        var request = CreateReactionPickerRequest(trigger, message);
        if (request is null) Execute(_viewModel?.OpenReactionPickerCommand, message);
        else Execute(_viewModel?.OpenReactionPickerAtCommand, request);
    }

    private ReactionPickerRequest? CreateReactionPickerRequest(VisualElement trigger, MessageItem message)
    {
        var source = trigger.Handler?.PlatformView as WinUiFrameworkElement;
        var host = Handler?.PlatformView as WinUiFrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<WinUiFrameworkElement>()
            .FirstOrDefault();
        if (source is null || host is null || pageRoot is null) return null;
        try
        {
            var triggerTopLeft = source.TransformToVisual(pageRoot)
                .TransformPoint(new WinPoint(0d, 0d));
            var hostTopLeft = host.TransformToVisual(pageRoot)
                .TransformPoint(new WinPoint(0d, 0d));
            var minimumX = hostTopLeft.X + ReactionPickerEdgeMargin;
            var maximumX = Math.Max(
                minimumX,
                hostTopLeft.X + host.ActualWidth - ReactionPickerWidth - ReactionPickerEdgeMargin);
            var preferredX = message.IsOwn
                ? triggerTopLeft.X + source.ActualWidth - ReactionPickerWidth
                : triggerTopLeft.X;
            return new ReactionPickerRequest(
                message,
                Math.Clamp(preferredX, minimumX, maximumX),
                triggerTopLeft.Y + source.ActualHeight);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

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
