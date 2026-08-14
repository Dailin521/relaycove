using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RelayCove.App.Controls;
using Windows.System;
using Windows.UI.Core;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerResizeBehavior : PlatformBehavior<ComposerResizeHandle, FrameworkElement>
{
    private const double MinimumHeight = 72d;
    private const double MaximumHeight = 300d;
    private const double KeyboardStep = 16d;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private readonly PointerEventHandler _pointerCanceledHandler;
    private readonly PointerEventHandler _pointerCaptureLostHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private FrameworkElement? _platformView;
    private Microsoft.UI.Xaml.Window? _platformWindow;
    private Pointer? _activePointer;
    private double _pointerStartY;
    private double _panStartHeight;
    private CoreCursor? _previousCursor;

    public ComposerResizeBehavior()
    {
        _pointerPressedHandler = OnPointerPressed;
        _pointerMovedHandler = OnPointerMoved;
        _pointerReleasedHandler = OnPointerReleased;
        _pointerCanceledHandler = OnPointerCanceled;
        _pointerCaptureLostHandler = OnPointerCaptureLost;
        _keyDownHandler = OnKeyDown;
    }

    public static readonly BindableProperty HeightProperty = BindableProperty.Create(
        nameof(Height),
        typeof(double),
        typeof(ComposerResizeBehavior),
        112d,
        BindingMode.TwoWay,
        coerceValue: static (_, value) => ClampHeight((double)value));

    public double Height
    {
        get => (double)GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    protected override void OnAttachedTo(ComposerResizeHandle bindable, FrameworkElement platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _platformView = platformView;
        platformView.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        platformView.AddHandler(UIElement.PointerMovedEvent, _pointerMovedHandler, true);
        platformView.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
        platformView.AddHandler(UIElement.PointerCanceledEvent, _pointerCanceledHandler, true);
        platformView.AddHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler, true);
        platformView.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, true);
        platformView.LostFocus += OnLostFocus;
        platformView.PointerEntered += OnPointerEntered;
        platformView.PointerExited += OnPointerExited;
        platformView.Unloaded += OnUnloaded;

        _platformWindow = Microsoft.Maui.Controls.Application.Current?.Windows
            .Select(window => window.Handler?.PlatformView)
            .OfType<Microsoft.UI.Xaml.Window>()
            .FirstOrDefault();
        if (_platformWindow is not null) _platformWindow.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFrom(ComposerResizeHandle bindable, FrameworkElement platformView)
    {
        EndDrag(releaseCapture: true);
        if (_platformWindow is not null) _platformWindow.Activated -= OnWindowActivated;
        _platformWindow = null;
        platformView.Unloaded -= OnUnloaded;
        platformView.PointerExited -= OnPointerExited;
        platformView.PointerEntered -= OnPointerEntered;
        platformView.LostFocus -= OnLostFocus;
        platformView.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
        platformView.RemoveHandler(UIElement.PointerCaptureLostEvent, _pointerCaptureLostHandler);
        platformView.RemoveHandler(UIElement.PointerCanceledEvent, _pointerCanceledHandler);
        platformView.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        platformView.RemoveHandler(UIElement.PointerMovedEvent, _pointerMovedHandler);
        platformView.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        RestoreCursor();
        _platformView = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is null || _activePointer is not null) return;
        var coordinateRoot = GetCoordinateRoot();
        var point = eventArgs.GetCurrentPoint(coordinateRoot);
        if (eventArgs.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_platformView.CapturePointer(eventArgs.Pointer)) return;
        _activePointer = eventArgs.Pointer;
        _pointerStartY = point.Position.Y;
        _panStartHeight = Height;
        _platformView.Focus(FocusState.Pointer);
        eventArgs.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_platformView is null || _activePointer?.PointerId != eventArgs.Pointer.PointerId) return;
        var point = eventArgs.GetCurrentPoint(GetCoordinateRoot());
        Height = CalculateHeight(_panStartHeight, _pointerStartY, point.Position.Y);
        eventArgs.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer?.PointerId != eventArgs.Pointer.PointerId) return;
        EndDrag(releaseCapture: true);
        eventArgs.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer?.PointerId != eventArgs.Pointer.PointerId) return;
        EndDrag(releaseCapture: true);
        eventArgs.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs eventArgs) => EndDrag(releaseCapture: false);

    private void OnLostFocus(object sender, RoutedEventArgs eventArgs) => EndDrag(releaseCapture: true);

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) => EndDrag(releaseCapture: true);

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs eventArgs)
    {
        if (eventArgs.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
        {
            EndDrag(releaseCapture: true);
        }
    }

    private UIElement GetCoordinateRoot() =>
        _platformView?.XamlRoot?.Content as UIElement ?? _platformView!;

    private void EndDrag(bool releaseCapture)
    {
        var pointer = _activePointer;
        _activePointer = null;
        if (releaseCapture && pointer is not null && _platformView is not null)
        {
            _platformView.ReleasePointerCapture(pointer);
        }
        RestoreCursor();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow is null) return;
        _previousCursor ??= coreWindow.PointerCursor;
        coreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeNorthSouth, 0);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer is null) RestoreCursor();
    }

    private void RestoreCursor()
    {
        if (_previousCursor is null) return;
        var coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow is not null) coreWindow.PointerCursor = _previousCursor;
        _previousCursor = null;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        var next = eventArgs.Key switch
        {
            VirtualKey.Up => Height + KeyboardStep,
            VirtualKey.Down => Height - KeyboardStep,
            VirtualKey.Home => MinimumHeight,
            VirtualKey.End => MaximumHeight,
            _ => double.NaN
        };

        if (!double.IsFinite(next)) return;
        Height = ClampHeight(next);
        eventArgs.Handled = true;
    }

    internal static double CalculateHeight(double startHeight, double startY, double currentY) =>
        ClampHeight(startHeight + startY - currentY);

    internal static double ClampHeight(double value) => Math.Clamp(value, MinimumHeight, MaximumHeight);
}
