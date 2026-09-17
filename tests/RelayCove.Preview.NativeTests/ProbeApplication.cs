using RelayCove.App.Controls;
using System.Reflection;

namespace RelayCove.Preview.NativeTests;

internal sealed class ProbeApplication : Application
{
    private readonly Grid surface = new() { BackgroundColor = Colors.DimGray };
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new ContentPage { Content = surface };
        var window = new Window(page) { Title = "ISOLATED preview regression — generated fixtures", Width = 780, Height = 620 };
        page.Loaded += async (_, _) =>
        {
            try { await RunAsync(window); }
            catch (Exception exception)
            {
                ProbeLog.Write("FAIL", $"HRESULT=0x{exception.HResult:X8} {exception}");
                Environment.Exit(1);
            }
        };
        return window;
    }

    private async Task RunAsync(Window window)
    {
        ProbeLog.Write("window-loaded");
        var diagnostics = typeof(RealmMediaImageView).Assembly.GetType("RelayCove.App.Controls.ImagePreviewDiagnostics");
        diagnostics?.GetProperty("Sink", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, (Action<string>)(message => ProbeLog.Write("product-diagnostic", message)));
        FixtureMediaService.GenerateFixtures();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            ProbeLog.Write("open-begin", iteration.ToString());
            View view;
            if (ProbeLog.Mode == "fixed")
                view = (View)Activator.CreateInstance(typeof(RealmMediaImageView).Assembly.GetType("RelayCove.App.Controls.ImagePreviewViewport", true)!)!;
            else view = new RealmMediaImageView { IsPreview = ProbeLog.Mode != "render-only" };
            view.Loaded += (_, _) =>
            {
                ProbeLog.Write("view-loaded", iteration.ToString());
                ConfigureOldProbe(view);
            };
            SetSource(view, "image.png");
            var wrapper = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Content = view,
                Margin = 20
            };
            var outer = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = wrapper,
                MaximumWidthRequest = 1120,
                MaximumHeightRequest = 760
            };
            surface.Children.Add(outer);
            await Task.Delay(iteration == 0 ? 1600 : 150);
            CheckImage(view, "PNG");
            if (iteration == 0)
            {
                ProbeLog.Write("idle-no-input-survived");
                foreach (var name in new[] { "image.jpg", "transparent.gif", "large.png" })
                {
                    SetSource(view, name);
                    await Task.Delay(550);
                    CheckImage(view, name);
                }
                if (ProbeLog.Mode == "fixed")
                {
                    Invoke(view, "ApplyZoom", 120, 180d, 140d);
                    var layer = (Grid)Field(view, "imageLayer")!;
                    if (Math.Abs(layer.Scale - 1.2) > 0.0001) throw new InvalidOperationException("Zoom did not reach 1.2.");
                    var beforeX = layer.TranslationX;
                    var beforeY = layer.TranslationY;
                    Invoke(view, "ApplyPan", 40d, -20d);
                    await Task.Delay(300);
                    if (Math.Abs(layer.TranslationX - beforeX - 40) > 0.0001 || Math.Abs(layer.TranslationY - beforeY + 20) > 0.0001)
                        throw new InvalidOperationException("Pan did not apply the requested displacement.");
                    if (layer.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement transformed || transformed.RenderTransform is null)
                        throw new InvalidOperationException("Transformed native layer missing.");
                    ProbeLog.Write("programmatic-zoom-pan");
                    Invoke(view, "ResetPreviewTransform");
                    AssertReset(view);
                    Invoke(view, "ApplyZoom", 120, 180d, 140d);
                }
                window.Width = 680;
                window.Height = 540;
                await Task.Delay(400);
                if (ProbeLog.Mode == "fixed" && Math.Abs(((Grid)Field(view, "imageLayer")!).Scale - 1.2) > 0.0001)
                    throw new InvalidOperationException("Resize changed the user's zoom.");
                ProbeLog.Write("resize-survived");
            }
            surface.Children.Remove(outer);
            await Task.Delay(80);
            if (ProbeLog.Mode == "fixed" && (Field(view, "inputRoot") is not null || Field(view, "dragPointerId") is not null))
                throw new InvalidOperationException("Preview input remained attached after unload.");
            ProbeLog.Write("close-complete", iteration.ToString());
        }
        var delayed = ProbeLog.Mode == "fixed"
            ? (View)Activator.CreateInstance(typeof(RealmMediaImageView).Assembly.GetType("RelayCove.App.Controls.ImagePreviewViewport", true)!)!
            : new RealmMediaImageView { IsPreview = true };
        SetSource(delayed, "delayed");
        surface.Children.Add(delayed);
        await Task.Delay(100);
        surface.Children.Remove(delayed);
        await Task.Delay(1800);
        if (FixtureMediaService.DelayedCancellations == 0) throw new InvalidOperationException("Delayed read was not canceled on unload.");
        ProbeLog.Write("delayed-close-complete");
        ProbeLog.Write("PASS", "20 open-close cycles; PNG/JPEG/GIF/large image; resize; delayed close");
        Environment.Exit(0);
    }

    private static void SetSource(View view, string value) => view.GetType().GetProperty("SourceUrl")!.SetValue(view, value);
    private static object? Field(View view, string name) => view.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);
    private static void AssertReset(View view)
    {
        var layer = (Grid)Field(view, "imageLayer")!;
        if (layer.Scale != 1 || layer.TranslationX != 0 || layer.TranslationY != 0) throw new InvalidOperationException("Reset did not restore the layer.");
        ProbeLog.Write("transform-reset-confirmed");
    }

    private static void ConfigureOldProbe(View view)
    {
        if (view is not RealmMediaImageView media || media.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement native) return;
        if (ProbeLog.Mode is "render-preview" or "clip-only" or "transform-only")
        {
            Invoke(view, "DetachPreviewInput");
            if (ProbeLog.Mode == "clip-only") native.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, 600, 400) };
            if (ProbeLog.Mode == "transform-only")
            {
                var image = (Image)typeof(RealmMediaImageView).GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media)!;
                image.Scale = 1.5;
                image.TranslationX = 25;
                image.TranslationY = -10;
            }
            ProbeLog.Write("probe-configured", ProbeLog.Mode);
        }
        if (ProbeLog.Mode == "events-only")
        {
            var method = typeof(RealmMediaImageView).GetMethod("OnPreviewSizeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            native.SizeChanged -= (Microsoft.UI.Xaml.SizeChangedEventHandler)method.CreateDelegate(typeof(Microsoft.UI.Xaml.SizeChangedEventHandler), media);
            native.Clip = null;
            ProbeLog.Write("probe-configured", "events attached, native clip removed");
        }
    }
    private static void Invoke(View view, string name, params object[] arguments) =>
        view.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(view, arguments);

    private static void CheckImage(View view, string name)
    {
        var media = FindMedia(view) ?? throw new InvalidOperationException("No real RealmMediaImageView found.");
        var image = (Image)typeof(RealmMediaImageView).GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media)!;
        if (media.IsFallbackVisible || !image.IsVisible || image.Source is null) throw new InvalidOperationException($"Fixture did not display: {name}");
        if (image.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.Image native || native.Source is null)
            throw new InvalidOperationException($"Native image has no source: {name}");
        if (native.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource bitmap && (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0))
            throw new InvalidOperationException($"Native image has no decoded pixels: {name}");
        if (name.EndsWith("gif", StringComparison.Ordinal) && typeof(RealmMediaImageView).GetField("_transparentGifTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media) is not IDispatcherTimer { IsRunning: true })
            throw new InvalidOperationException("Transparent GIF did not use the running frame renderer.");
        ProbeLog.Write("native-image-ready", $"{name} {native.ActualWidth}x{native.ActualHeight} source={native.Source.GetType().Name}");
    }

    private static RealmMediaImageView? FindMedia(IVisualTreeElement element)
    {
        if (element is RealmMediaImageView media) return media;
        foreach (var child in element.GetVisualChildren()) if (FindMedia(child) is { } found) return found;
        return null;
    }
}
