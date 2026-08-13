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

    public RealmMediaImageView()
    {
        _image = new Image { Aspect = Aspect.AspectFit, IsVisible = false };
        _loading = new ActivityIndicator { IsRunning = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _fallback = new Label
        {
            Text = "无法加载受控图片",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            IsVisible = false
        };
        Content = new Grid { Children = { _image, _loading, _fallback } };
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

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        _ = ReloadAsync();
    }

    private static void OnSourceChanged(BindableObject bindable, object oldValue, object newValue) =>
        _ = ((RealmMediaImageView)bindable).ReloadAsync();

    private async Task ReloadAsync()
    {
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _image.Source = null;
        _image.IsVisible = false;
        _fallback.IsVisible = false;
        if (Handler?.MauiContext?.Services.GetService<IRealmMediaService>() is not { } service ||
            string.IsNullOrWhiteSpace(SourceUrl))
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
            return;
        }
        var current = new CancellationTokenSource();
        _loadCancellation = current;
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        try
        {
            var source = await service.GetImageAsync(SourceUrl, MediaKind, current.Token);
            if (current.IsCancellationRequested) return;
            _image.Source = source;
            _image.IsVisible = true;
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch
        {
            if (!current.IsCancellationRequested && ShowFailureText) _fallback.IsVisible = true;
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
}
