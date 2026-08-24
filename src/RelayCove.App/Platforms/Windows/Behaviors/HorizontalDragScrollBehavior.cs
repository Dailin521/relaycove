using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinUiScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class HorizontalDragScrollBehavior : PlatformBehavior<ScrollView, WinUiScrollViewer>
{
    private const double DragThreshold = 4d;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private readonly PointerEventHandler _pointerCanceledHandler;
    private readonly PointerEventHandler _pointerCaptureLostHandler;
    private WinUiScrollViewer? _platformView;
    private Pointer? _activePointer;
    private double _startX;
    private double _startOffset;
    private bool _isDragging;

    public HorizontalDragScrollBehavior()
    {
        _pointerPressedHandler = OnPointerPressed;
        _pointerMovedHandler = OnPointerMoved;
        _pointerReleasedHandler = OnPointerReleased;
        _pointerCanceledHandler = OnPointerCanceled;
        _pointerCaptureLostHandler = OnPointerCaptureLost;
    }

    protected override void OnAttachedTo(ScrollView bindable, WinUiScrollViewer platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _platformView = platformView;
        platformView.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        platformView.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
        platformView.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
        platformView.AddHandler(UIElement.PointerCanceledEvent, _pointerCanceledHandler, true);
        platformView.AddHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler, true);
        platformView.Unloaded += OnUnloaded;
    }

    protected override void OnDetachedFrom(ScrollView bindable, WinUiScrollViewer platformView)
    {
        EndDrag(releaseCapture: true);
        platformView.Unloaded -= OnUnloaded;
        platformView.RemoveHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler);
        platformView.RemoveHandler(UIElement.PointerCanceledEvent, _pointerCanceledHandler);
        platformView.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        platformView.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        platformView.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        _platformView = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is null || _activePointer is not null ||
            eventArgs.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
        {
            return;
        }

        var point = eventArgs.GetCurrentPoint(_platformView);
        if (!point.Properties.IsLeftButtonPressed) return;

        _activePointer = eventArgs.Pointer;
        _startX = point.Position.X;
        _startOffset = _platformView.HorizontalOffset;
        _isDragging = false;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is null || _activePointer?.PointerId != eventArgs.Pointer.PointerId) return;

        var currentX = eventArgs.GetCurrentPoint(_platformView).Position.X;
        if (!_isDragging)
        {
            if (!ShouldStartDrag(_startX, currentX)) return;
            _isDragging = true;
            _ = _platformView.CapturePointer(eventArgs.Pointer);
        }

        var offset = CalculateOffset(_startOffset, _startX, currentX, _platformView.ScrollableWidth);
        _platformView.ChangeView(offset, null, null, disableAnimation: true);
        eventArgs.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer?.PointerId != eventArgs.Pointer.PointerId) return;
        var wasDragging = _isDragging;
        EndDrag(releaseCapture: true);
        if (wasDragging) eventArgs.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer?.PointerId != eventArgs.Pointer.PointerId) return;
        EndDrag(releaseCapture: true);
        eventArgs.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs eventArgs) =>
        EndDrag(releaseCapture: false);

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) => EndDrag(releaseCapture: true);

    private void EndDrag(bool releaseCapture)
    {
        var pointer = _activePointer;
        _activePointer = null;
        _isDragging = false;
        if (releaseCapture && pointer is not null && _platformView is not null)
        {
            _platformView.ReleasePointerCapture(pointer);
        }
    }

    internal static bool ShouldStartDrag(double startX, double currentX) =>
        Math.Abs(currentX - startX) >= DragThreshold;

    internal static double CalculateOffset(
        double startOffset,
        double startX,
        double currentX,
        double scrollableWidth) =>
        Math.Clamp(startOffset + startX - currentX, 0d, Math.Max(0d, scrollableWidth));
}
