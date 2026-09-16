using Microsoft.Extensions.DependencyInjection;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public sealed class StickerImageView : ContentView
{
    private readonly Image _image = new() { Aspect = Aspect.AspectFit, IsAnimationPlaying = false };
    private readonly ActivityIndicator _loading = new() { WidthRequest = 20, HeightRequest = 20 };
    private readonly Label _failure = new() { Text = "加载失败", FontSize = 11, IsVisible = false, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
    private CancellationTokenSource? _load;
    private bool _visible;
    private bool _hovered;

    public StickerImageView()
    {
        Content = new Grid { Children = { _image, _loading, _failure } };
        Loaded += (_, _) => { _visible = true; _ = ReloadAsync(); };
        Unloaded += (_, _) => { _visible = false; Clear(); };
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) =>
        {
            _hovered = true;
            if (Item?.Catalog?.Animated == true) _ = ReloadAsync(original: true);
            else _image.IsAnimationPlaying = true;
        };
        pointer.PointerExited += (_, _) =>
        {
            _hovered = false;
            _image.IsAnimationPlaying = false;
            if (Item?.Catalog?.Animated == true) _ = ReloadAsync();
        };
        GestureRecognizers.Add(pointer);
    }

    public static readonly BindableProperty ItemProperty = BindableProperty.Create(nameof(Item), typeof(StickerPickerItem),
        typeof(StickerImageView), propertyChanged: static (bindable, _, _) =>
        {
            var view = (StickerImageView)bindable;
            view._hovered = false;
            view.Clear();
            _ = view.ReloadAsync();
        });

    public StickerPickerItem? Item
    {
        get => (StickerPickerItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void Clear()
    {
        _load?.Cancel();
        _image.IsAnimationPlaying = false;
        _image.Source = null;
    }

    private async Task ReloadAsync(bool original = false)
    {
        _load?.Cancel();
        if (!_visible || Item is not { } item || Handler?.MauiContext?.Services.GetService<StickerPickerViewModel>() is not { } model) return;
        using var cancellation = new CancellationTokenSource();
        _load = cancellation;
        _loading.IsVisible = _loading.IsRunning = _image.Source is null;
        _failure.IsVisible = false;
        try
        {
            var media = await model.LoadImageAsync(item, !original, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_visible || Item != item) return;
            _image.IsAnimationPlaying = _hovered;
            _image.Source = ImageSource.FromStream(() => new MemoryStream(media.Content, writable: false));
        }
        catch (OperationCanceledException) { }
        catch { if (!cancellation.IsCancellationRequested) _failure.IsVisible = _image.Source is null; }
        finally
        {
            if (ReferenceEquals(_load, cancellation))
            {
                _load = null;
                _loading.IsRunning = _loading.IsVisible = false;
            }
        }
    }
}
