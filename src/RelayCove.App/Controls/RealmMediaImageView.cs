using Microsoft.Extensions.DependencyInjection;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Controls;

public sealed class RealmMediaImageView : ContentView
{
    private readonly Image _image;
    private readonly ActivityIndicator _loading;
    private readonly Label _fallback;
    private CancellationTokenSource? _loadCancellation;
    private string? _loadedSourceKey;
    private IRealmMediaService? _mediaService;
    private IClientSession? _session;
    private AccountId? _displayAccountId;
    private volatile string? _requestedSourceKey;

    public RealmMediaImageView()
    {
        _image = new Image { Aspect = Aspect.AspectFit, IsVisible = false, IsAnimationPlaying = true };
        _loading = new ActivityIndicator
        {
            IsRunning = true,
            WidthRequest = 28,
            HeightRequest = 28,
            Margin = 8,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        _fallback = new Label
        {
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            IsVisible = false
        };
        Content = new Grid { Children = { _image, _loading, _fallback } };
        Loaded += (_, _) => AttachServices();
        Unloaded += (_, _) => DetachServices();
    }

    public static readonly BindableProperty SourceUrlProperty = BindableProperty.Create(
        nameof(SourceUrl),
        typeof(string),
        typeof(RealmMediaImageView),
        propertyChanged: OnSourceChanged);

    public static readonly BindableProperty MediaKindProperty = BindableProperty.Create(
        nameof(MediaKind),
        typeof(RealmMediaKind),
        typeof(RealmMediaImageView),
        RealmMediaKind.Image,
        propertyChanged: OnSourceChanged);

    public static readonly BindableProperty AspectProperty = BindableProperty.Create(
        nameof(Aspect),
        typeof(Aspect),
        typeof(RealmMediaImageView),
        Aspect.AspectFit,
        propertyChanged: static (bindable, _, value) =>
            ((RealmMediaImageView)bindable)._image.Aspect = (Aspect)value);

    public static readonly BindableProperty ShowFailureTextProperty = BindableProperty.Create(
        nameof(ShowFailureText),
        typeof(bool),
        typeof(RealmMediaImageView),
        true);

    public static readonly BindableProperty IsPreviewProperty = BindableProperty.Create(
        nameof(IsPreview),
        typeof(bool),
        typeof(RealmMediaImageView),
        false);

    private static readonly BindablePropertyKey IsFallbackVisiblePropertyKey = BindableProperty.CreateReadOnly(
        nameof(IsFallbackVisible),
        typeof(bool),
        typeof(RealmMediaImageView),
        true);

    public static readonly BindableProperty IsFallbackVisibleProperty = IsFallbackVisiblePropertyKey.BindableProperty;

    public bool IsFallbackVisible => (bool)GetValue(IsFallbackVisibleProperty);

    public string? SourceUrl
    {
        get => (string?)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public RealmMediaKind MediaKind
    {
        get => (RealmMediaKind)GetValue(MediaKindProperty);
        set => SetValue(MediaKindProperty, value);
    }

    public Aspect Aspect
    {
        get => (Aspect)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public bool ShowFailureText
    {
        get => (bool)GetValue(ShowFailureTextProperty);
        set => SetValue(ShowFailureTextProperty, value);
    }

    public bool IsPreview
    {
        get => (bool)GetValue(IsPreviewProperty);
        set => SetValue(IsPreviewProperty, value);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        AttachServices();
    }

    private void DetachServices()
    {
        _image.IsAnimationPlaying = false;
        if (_mediaService is not null) _mediaService.AvatarChanged -= OnAvatarChanged;
        if (_session is not null) _session.StateChanged -= OnSessionStateChanged;
        _mediaService = null;
        _session = null;
        _loadCancellation?.Cancel();
    }

    private void AttachServices()
    {
        DetachServices();
        _image.IsAnimationPlaying = true;
        _mediaService = Handler?.MauiContext?.Services.GetService<IRealmMediaService>();
        _session = Handler?.MauiContext?.Services.GetService<IClientSession>();
        if (_mediaService is not null) _mediaService.AvatarChanged += OnAvatarChanged;
        if (_session is not null) _session.StateChanged += OnSessionStateChanged;
        var preserveImage = _image.Source is not null && _displayAccountId == _session?.AccountId;
        _loadedSourceKey = null;
        _ = ReloadAsync(preserveImage);
    }

    private void OnAvatarChanged(object? sender, AvatarChangedEventArgs args)
    {
        var sourceKey = $"{args.AccountId.Value}:{RealmMediaKind.Avatar}:{args.SourceUrl}";
        if (_requestedSourceKey != sourceKey) return;
        Dispatcher.Dispatch(() =>
        {
            if (_requestedSourceKey != sourceKey || _session?.AccountId != args.AccountId) return;
            _loadedSourceKey = null;
            _ = ReloadAsync(preserveImage: true);
        });
    }

    private void OnSessionStateChanged(object? sender, ClientStateChangedEventArgs args) => Dispatcher.Dispatch(() =>
    {
        if (_displayAccountId != _session?.AccountId) _ = ReloadAsync();
    });

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        _ = ReloadAsync();
    }

    private static void OnSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (RealmMediaImageView)bindable;
        var compact = view.MediaKind == RealmMediaKind.Emoji;
        view._loading.WidthRequest = view._loading.HeightRequest = compact ? 14 : 28;
        view._loading.Margin = compact ? 0 : 8;
        _ = view.ReloadAsync();
    }

    private async Task ReloadAsync(bool preserveImage = false)
    {
        var services = Handler?.MauiContext?.Services;
        var service = services?.GetService<IRealmMediaService>();
        var accountId = services?.GetService<IClientSession>()?.AccountId;
        _displayAccountId = accountId;
        var sourceKey = $"{accountId?.Value ?? "signed-out"}:{MediaKind}:{SourceUrl}";
        _requestedSourceKey = sourceKey;
        // CollectionView may reapply an unchanged binding while it reconciles a
        // navigation row.  Keeping the decoded image avoids the visible blank /
        // spinner flash caused by clearing and downloading the same avatar again.
        if (service is not null && string.Equals(_loadedSourceKey, sourceKey, StringComparison.Ordinal) &&
            _image.Source is not null)
        {
            return;
        }

        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (!preserveImage)
        {
            _image.Source = null;
            _image.IsVisible = false;
            SetValue(IsFallbackVisiblePropertyKey, true);
        }
        _fallback.IsVisible = false;
        if (service is null ||
            string.IsNullOrWhiteSpace(SourceUrl))
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
            return;
        }
        var current = new CancellationTokenSource();
        var cancellationToken = current.Token;
        _loadCancellation = current;
        _loading.IsVisible = MediaKind != RealmMediaKind.Avatar && !preserveImage;
        _loading.IsRunning = _loading.IsVisible;
        try
        {
            // Let the newly visible preview and spinner render before any media
            // work, including a synchronous cache hit followed by native decoding.
            if (IsPreview && Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement element)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => ready.TrySetResult())) return;
                await ready.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var source = await service.GetImageAsync(SourceUrl, MediaKind, cancellationToken);
            if (current.IsCancellationRequested || accountId != _session?.AccountId) return;
            // Keep the spinner until pixels are ready, not just the downloaded
            // stream. The shared source service reuses this decode for the Image.
            using var decodedImage = IsPreview
                ? await DecodePreviewAsync(source, cancellationToken)
                : null;
            if (current.IsCancellationRequested || accountId != _session?.AccountId) return;
            _image.Source = source;
            _loadedSourceKey = sourceKey;
            _image.IsVisible = true;
            SetValue(IsFallbackVisiblePropertyKey, false);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!current.IsCancellationRequested)
            {
                _loadedSourceKey = null;
                _fallback.Text = RealmMediaFailureMessage.FromException(exception);
                _fallback.IsVisible = ShowFailureText;
            }
        }
        finally
        {
            if (!current.IsCancellationRequested)
            {
                _loading.IsRunning = false;
                _loading.IsVisible = false;
            }
        }
    }

    private async Task<IImageSourceServiceResult<Microsoft.UI.Xaml.Media.ImageSource>?> DecodePreviewAsync(
        ImageSource source,
        CancellationToken cancellationToken)
    {
        var provider = Handler!.MauiContext!.Services.GetRequiredService<IImageSourceServiceProvider>();
        var decoder = provider.GetImageSourceService(source.GetType())
            ?? throw new InvalidOperationException("No image decoder is available.");
        return await decoder.GetImageSourceAsync(source, cancellationToken: cancellationToken);
    }
}
