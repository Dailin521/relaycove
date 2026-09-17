using NativeImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace RelayCove.App.Platforms.Windows;

internal sealed class RealmImageSourceService : StreamImageSourceService, IImageSourceService<RealmImageSource>
{
    private readonly DecodedImageCache<NativeImageSource> _images = new();

    public override async Task<IImageSourceServiceResult<NativeImageSource>?> GetImageSourceAsync(
        IImageSource imageSource,
        float scale = 1,
        CancellationToken cancellationToken = default)
    {
        var image = await ImageLoadCancellationPolicy.ReturnNullWhenCanceledAsync(() =>
        {
            return _images.GetAsync(imageSource, async token =>
            {
                using var result = await base.GetImageSourceAsync(imageSource, scale, token);
                return result?.Value;
            }, cancellationToken);
        }, cancellationToken);
        return image is null ? null : new ImageSourceServiceResult(image);
    }
}
