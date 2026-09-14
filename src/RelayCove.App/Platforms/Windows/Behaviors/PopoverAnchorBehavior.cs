using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinUiBorder = Microsoft.Maui.Platform.ContentPanel;
using WinUiFrameworkElement = Microsoft.UI.Xaml.FrameworkElement;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class PopoverAnchorBehavior : Behavior<Border>
{
    private const double EdgeMargin = 12d;
    private WinUiBorder? _platformView;
    private Border? _virtualView;
    private WinUiFrameworkElement? _root;

    public static readonly BindableProperty IsOpenProperty = BindableProperty.Create(
        nameof(IsOpen),
        typeof(bool),
        typeof(PopoverAnchorBehavior),
        true,
        propertyChanged: OnAnchorChanged);

    public static readonly BindableProperty AnchorXProperty = BindableProperty.Create(
        nameof(AnchorX),
        typeof(double),
        typeof(PopoverAnchorBehavior),
        propertyChanged: OnAnchorChanged);

    public static readonly BindableProperty AnchorYProperty = BindableProperty.Create(
        nameof(AnchorY),
        typeof(double),
        typeof(PopoverAnchorBehavior),
        propertyChanged: OnAnchorChanged);

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double AnchorX
    {
        get => (double)GetValue(AnchorXProperty);
        set => SetValue(AnchorXProperty, value);
    }

    public double AnchorY
    {
        get => (double)GetValue(AnchorYProperty);
        set => SetValue(AnchorYProperty, value);
    }

    protected override void OnAttachedTo(Border bindable)
    {
        base.OnAttachedTo(bindable);
        _virtualView = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        Attach(bindable.Handler?.PlatformView as WinUiBorder);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        Detach();
        _virtualView = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        Attach((sender as Border)?.Handler?.PlatformView as WinUiBorder);

    private void Attach(WinUiBorder? platformView)
    {
        Detach();
        if (platformView is null) return;
        _platformView = platformView;
        platformView.Loaded += OnLayoutChanged;
        platformView.SizeChanged += OnSizeChanged;
        SchedulePositionUpdate();
    }

    private void Detach()
    {
        if (_root is not null) _root.SizeChanged -= OnSizeChanged;
        _root = null;
        if (_platformView is null) return;
        _platformView.Loaded -= OnLayoutChanged;
        _platformView.SizeChanged -= OnSizeChanged;
        _platformView.LayoutUpdated -= OnLayoutUpdated;
        _platformView = null;
    }

    private void OnLayoutChanged(object sender, RoutedEventArgs eventArgs) => SchedulePositionUpdate();
    private void OnSizeChanged(object sender, SizeChangedEventArgs eventArgs) => SchedulePositionUpdate();

    private static void OnAnchorChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((PopoverAnchorBehavior)bindable).SchedulePositionUpdate();

    private void SchedulePositionUpdate()
    {
        if (_platformView is not { } popover) return;
        popover.LayoutUpdated -= OnLayoutUpdated;
        if (!IsOpen) return;
        // Reopening at the same cursor position need not change size or anchors.
        // Wait for this visibility/layout pass before measuring the page offset.
        popover.LayoutUpdated += OnLayoutUpdated;
        popover.InvalidateArrange();
    }

    private void OnLayoutUpdated(object? sender, object eventArgs)
    {
        if (_platformView is not { } popover) return;
        popover.LayoutUpdated -= OnLayoutUpdated;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var popover = _platformView;
        var root = popover is null ? null : GetRoot(popover);
        if (!IsOpen || _virtualView is null || popover is null || root is null ||
            root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        if (!ReferenceEquals(_root, root))
        {
            if (_root is not null) _root.SizeChanged -= OnSizeChanged;
            _root = root;
            _root.SizeChanged += OnSizeChanged;
        }
        var width = popover.ActualWidth;
        var height = popover.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var (x, y) = CalculatePosition(AnchorX, AnchorY, width, height, root.ActualWidth, root.ActualHeight);
        var currentPosition = popover.TransformToVisual(root)
            .TransformPoint(new global::Windows.Foundation.Point(0d, 0d));
        // MAUI owns RenderTransform. Use its properties so a later arrange or
        // visibility update cannot overwrite our native-only translation.
        _virtualView.TranslationX = CalculateRelativeTranslation(x, currentPosition.X, _virtualView.TranslationX);
        _virtualView.TranslationY = CalculateRelativeTranslation(y, currentPosition.Y, _virtualView.TranslationY);
    }

    internal static (double X, double Y) CalculatePosition(
        double anchorX, double anchorY, double width, double height, double pageWidth, double pageHeight)
    {
        var x = anchorX;
        if (x + width > pageWidth - EdgeMargin) x = anchorX - width;
        x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, pageWidth - width - EdgeMargin));
        var y = anchorY + 4d;
        if (y + height > pageHeight - EdgeMargin) y = anchorY - height - 4d;
        y = Math.Clamp(y, EdgeMargin, Math.Max(EdgeMargin, pageHeight - height - EdgeMargin));
        return (x, y);
    }

    internal static double CalculateRelativeTranslation(
        double targetPosition,
        double currentPosition,
        double currentTranslation) =>
        targetPosition - (currentPosition - currentTranslation);

    private static WinUiFrameworkElement? GetRoot(DependencyObject element)
    {
        var pageRoot = Microsoft.Maui.Controls.Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<WinUiFrameworkElement>()
            .FirstOrDefault();
        if (pageRoot is not null) return pageRoot;
        DependencyObject current = element;
        while (VisualTreeHelper.GetParent(current) is { } parent) current = parent;
        return current as WinUiFrameworkElement;
    }
}
