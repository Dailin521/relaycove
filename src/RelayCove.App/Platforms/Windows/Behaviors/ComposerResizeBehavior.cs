using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using ReflectionPropertyInfo = System.Reflection.PropertyInfo;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ComposerResizeBehavior : PlatformBehavior<Button, FrameworkElement>
{
    private const double MinimumHeight = 128d;
    private const double MaximumHeight = 300d;
    private const double KeyboardStep = 16d;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerMovedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private readonly PointerEventHandler _pointerCanceledHandler;
    private readonly PointerEventHandler _pointerCaptureLostHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private static readonly ReflectionPropertyInfo? ProtectedCursorProperty = typeof(UIElement).GetProperty(
        "ProtectedCursor",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private FrameworkElement? _platformView;
    private Microsoft.UI.Xaml.Window? _platformWindow;
    private Pointer? _activePointer;
    private double _pointerStartY;
    private double _panStartHeight;

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
        128d,
        BindingMode.TwoWay,
        coerceValue: static (_, value) => ClampHeight((double)value));

    public double Height
    {
        get => (double)GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    protected override void OnAttachedTo(Button bindable, FrameworkElement platformView)
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

    protected override void OnDetachedFrom(Button bindable, FrameworkElement platformView)
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
        SetCursor(InputSystemCursorShape.Arrow);
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

        // A native Button may already capture its own pointer before this
        // handled routed event reaches the behavior. That capture is enough
        // for the drag; do not mistake a second CapturePointer false result
        // for a rejected gesture.
        _ = _platformView.CapturePointer(eventArgs.Pointer);
        _activePointer = eventArgs.Pointer;
        _pointerStartY = point.Position.Y;
        _panStartHeight = Height;
        _platformView.Focus(FocusState.Pointer);
        eventArgs.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            // WinUI can restore the Button's default arrow after PointerEntered.
            // Reapply on every hover move so the resize affordance stays visible.
            SetCursor(InputSystemCursorShape.SizeNorthSouth);
        }

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
        SetCursor(InputSystemCursorShape.Arrow);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (eventArgs.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            SetCursor(InputSystemCursorShape.SizeNorthSouth);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs eventArgs)
    {
        if (_activePointer is null) SetCursor(InputSystemCursorShape.Arrow);
    }

    private void SetCursor(InputSystemCursorShape cursorShape)
    {
        if (_platformView is null || ProtectedCursorProperty is null) return;
        ProtectedCursorProperty.SetValue(_platformView, InputSystemCursor.Create(cursorShape));
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
