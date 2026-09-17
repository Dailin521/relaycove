using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NativeElement = Microsoft.UI.Xaml.FrameworkElement;

namespace RelayCove.App.Controls;

/// <summary>Owns preview input and transforms without altering the media control's native clip.</summary>
public sealed class ImagePreviewViewport : ContentView
{
    private readonly Grid viewport;
    private readonly Grid imageLayer;
    private readonly RealmMediaImageView image;
    private readonly ImagePreviewTransform transform = new();
    private NativeElement? inputRoot;
    private bool loaded;
    private uint? dragPointerId;
    private global::Windows.Foundation.Point previousPoint;

    public ImagePreviewViewport()
    {
        image = new RealmMediaImageView { IsPreview = true, Aspect = Aspect.AspectFit };
        imageLayer = new Grid { Children = { image } };
        viewport = new Grid { IsClippedToBounds = true, BackgroundColor = Colors.Transparent, Children = { imageLayer } };
        Content = viewport;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        viewport.HandlerChanged += OnViewportHandlerChanged;
        viewport.HandlerChanging += OnViewportHandlerChanging;
        ImagePreviewDiagnostics.Record("viewport-created");
    }

    public static readonly BindableProperty SourceUrlProperty = BindableProperty.Create(
        nameof(SourceUrl), typeof(string), typeof(ImagePreviewViewport), propertyChanged: OnSourceChanged);

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public void ResetPreviewTransform()
    {
        StopDrag();
        transform.Reset();
        ApplyTransform();
    }

    internal void ApplyZoom(int delta, double x, double y)
    {
        transform.Zoom(delta, x, y, viewport.Width, viewport.Height);
        ApplyTransform();
    }

    internal void ApplyPan(double deltaX, double deltaY)
    {
        transform.Pan(deltaX, deltaY);
        ApplyTransform();
    }

    private static void OnSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (ImagePreviewViewport)bindable;
        view.ResetPreviewTransform();
        view.image.SourceUrl = (string?)newValue;
        ImagePreviewDiagnostics.Record("viewport-source-changed");
    }

    private void OnLoaded(object? sender, EventArgs args)
    {
        loaded = true;
        AttachInput();
        ImagePreviewDiagnostics.Record("viewport-loaded");
    }

    private void OnUnloaded(object? sender, EventArgs args)
    {
        loaded = false;
        DetachInput();
        ResetPreviewTransform();
        ImagePreviewDiagnostics.Record("viewport-unloaded");
    }

    private void OnViewportHandlerChanging(object? sender, HandlerChangingEventArgs args) => DetachInput();
    private void OnViewportHandlerChanged(object? sender, EventArgs args) => AttachInput();

    private void AttachInput()
    {
        var root = loaded ? viewport.Handler?.PlatformView as NativeElement : null;
        if (ReferenceEquals(root, inputRoot)) return;
        DetachInput();
        if (root is null) return;
        inputRoot = root;
        root.PointerWheelChanged += OnWheel;
        root.PointerPressed += OnPressed;
        root.PointerMoved += OnMoved;
        root.PointerReleased += OnReleased;
        root.PointerCanceled += OnReleased;
        root.PointerCaptureLost += OnCaptureLost;
        ImagePreviewDiagnostics.Record("input-attached");
    }

    private void DetachInput()
    {
        StopDrag();
        if (inputRoot is not { } root) return;
        root.PointerWheelChanged -= OnWheel;
        root.PointerPressed -= OnPressed;
        root.PointerMoved -= OnMoved;
        root.PointerReleased -= OnReleased;
        root.PointerCanceled -= OnReleased;
        root.PointerCaptureLost -= OnCaptureLost;
        inputRoot = null;
        ImagePreviewDiagnostics.Record("input-detached");
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        if (inputRoot is not { } root || image.IsFallbackVisible) return;
        var point = args.GetCurrentPoint(root);
        if (point.Properties.IsHorizontalMouseWheel) return;
        ApplyZoom(point.Properties.MouseWheelDelta, point.Position.X, point.Position.Y);
        args.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        if (inputRoot is not { } root || dragPointerId is not null || image.IsFallbackVisible
            || args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var point = args.GetCurrentPoint(root);
        if (!point.Properties.IsLeftButtonPressed || !root.CapturePointer(args.Pointer)) return;
        dragPointerId = args.Pointer.PointerId;
        previousPoint = point.Position;
        args.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        if (inputRoot is not { } root || dragPointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(root);
        if (!point.Properties.IsLeftButtonPressed) { StopDrag(); return; }
        ApplyPan(point.Position.X - previousPoint.X, point.Position.Y - previousPoint.Y);
        previousPoint = point.Position;
        args.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointerId != args.Pointer.PointerId) return;
        StopDrag();
        args.Handled = true;
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs args) => dragPointerId = null;

    private void StopDrag()
    {
        if (dragPointerId is null) return;
        dragPointerId = null;
        inputRoot?.ReleasePointerCaptures();
    }

    private void ApplyTransform()
    {
        imageLayer.Scale = transform.Scale;
        imageLayer.TranslationX = transform.X;
        imageLayer.TranslationY = transform.Y;
    }
}
