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
        bindable.HandlerChanged += OnHandlerChanged;
        Attach(bindable.Handler?.PlatformView as WinUiBorder);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        Detach();
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
        UpdatePosition();
    }

    private void Detach()
    {
        if (_platformView is null) return;
        _platformView.Loaded -= OnLayoutChanged;
        _platformView.SizeChanged -= OnSizeChanged;
        _platformView = null;
    }

    private void OnLayoutChanged(object sender, RoutedEventArgs eventArgs) => UpdatePosition();
    private void OnSizeChanged(object sender, SizeChangedEventArgs eventArgs) => UpdatePosition();

    private static void OnAnchorChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((PopoverAnchorBehavior)bindable).UpdatePosition();

    private void UpdatePosition()
    {
        var popover = _platformView;
        var root = popover is null ? null : GetRoot(popover);
        if (popover is null || root is null || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        var width = popover.ActualWidth > 0 ? popover.ActualWidth : popover.DesiredSize.Width;
        var height = popover.ActualHeight > 0 ? popover.ActualHeight : popover.DesiredSize.Height;
        if (width <= 0 || height <= 0) return;
        var x = AnchorX;
        if (x + width > root.ActualWidth - EdgeMargin) x = AnchorX - width;
        x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, root.ActualWidth - width - EdgeMargin));
        var y = AnchorY + 4d;
        if (y + height > root.ActualHeight - EdgeMargin) y = AnchorY - height - 4d;
        y = Math.Clamp(y, EdgeMargin, Math.Max(EdgeMargin, root.ActualHeight - height - EdgeMargin));
        popover.RenderTransform = new TranslateTransform { X = x, Y = y };
    }

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
