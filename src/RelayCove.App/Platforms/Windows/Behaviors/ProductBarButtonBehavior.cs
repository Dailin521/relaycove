using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinUiButton = Microsoft.UI.Xaml.Controls.Button;
using WinUiSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class ProductBarButtonBehavior : PlatformBehavior<ImageButton, WinUiButton>
{
    private const double NormalOpacity = 0.72d;
    private const double HoverOpacity = 1d;
    private const double PressedOpacity = 0.55d;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly PointerEventHandler _pointerReleasedHandler;
    private ImageButton? _virtualView;
    private WinUiButton? _platformView;
    private bool _isPointerOver;

    public ProductBarButtonBehavior()
    {
        _pointerPressedHandler = OnPointerPressed;
        _pointerReleasedHandler = OnPointerReleased;
    }

    protected override void OnAttachedTo(ImageButton bindable, WinUiButton platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _virtualView = bindable;
        _platformView = platformView;
        bindable.Opacity = NormalOpacity;
        ClearNativeBackground();
        platformView.PointerEntered += OnPointerEntered;
        platformView.PointerExited += OnPointerExited;
        platformView.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
        platformView.AddHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler, true);
    }

    protected override void OnDetachedFrom(ImageButton bindable, WinUiButton platformView)
    {
        platformView.RemoveHandler(UIElement.PointerReleasedEvent, _pointerReleasedHandler);
        platformView.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
        platformView.PointerExited -= OnPointerExited;
        platformView.PointerEntered -= OnPointerEntered;
        bindable.Opacity = 1d;
        _isPointerOver = false;
        _platformView = null;
        _virtualView = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs eventArgs)
    {
        _isPointerOver = true;
        ApplyIconState(HoverOpacity);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs eventArgs)
    {
        _isPointerOver = false;
        ApplyIconState(NormalOpacity);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs eventArgs) =>
        ApplyIconState(PressedOpacity);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs eventArgs) =>
        ApplyIconState(_isPointerOver ? HoverOpacity : NormalOpacity);

    private void ApplyIconState(double opacity)
    {
        if (_virtualView is not null) _virtualView.Opacity = opacity;
        ClearNativeBackground();
    }

    private void ClearNativeBackground()
    {
        if (_platformView is not null)
            _platformView.Background = new WinUiSolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
