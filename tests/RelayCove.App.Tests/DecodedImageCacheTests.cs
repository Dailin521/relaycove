using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class DecodedImageCacheTests
{
    [Fact]
    public void GetImageSourceService_WhenRegistered_UsesOneSharedDecoder()
    {
        using var app = MauiApp.CreateBuilder(useDefaults: false)
            .ConfigureImageSources(sources => sources.AddService<RealmImageSource, RealmImageSourceService>())
            .Build();
        var provider = app.Services.GetRequiredService<IImageSourceServiceProvider>();

        var decoder = provider.GetImageSourceService(typeof(RealmImageSource));

        Assert.IsType<RealmImageSourceService>(decoder);
        Assert.Same(decoder, provider.GetImageSourceService(typeof(RealmImageSource)));
    }

    [Fact]
    public async Task GetAsync_WhenThumbnailIsStillAlive_ViewerReusesDecode()
    {
        var cache = new DecodedImageCache<object>();
        var source = new object();
        var thumbnail = await cache.GetAsync(source, _ => Task.FromResult<object?>(new object()));

        var viewer = await cache.GetAsync(source, _ => throw new InvalidOperationException("Unexpected decode."));

        Assert.Same(thumbnail, viewer);
    }

    [Fact]
    public async Task GetAsync_WhenSourceInstanceChanges_DoesNotReuseAnotherAccountOrImage()
    {
        var cache = new DecodedImageCache<object>();
        var first = await cache.GetAsync(new object(), _ => Task.FromResult<object?>(new object()));
        var second = await cache.GetAsync(new object(), _ => Task.FromResult<object?>(new object()));

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task GetAsync_WhenThumbnailIsStillDecoding_ViewerWaitsForSameDecode()
    {
        var cache = new DecodedImageCache<object>();
        var source = new object();
        var decoded = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thumbnail = cache.GetAsync(source, _ => decoded.Task);
        var viewer = cache.GetAsync(source, _ => throw new InvalidOperationException("Unexpected decode."));

        Assert.False(viewer.IsCompleted);
        decoded.SetResult(new object());

        Assert.Same(await thumbnail, await viewer);
    }

    [Fact]
    public async Task GetAsync_WhenViewerIsCanceled_ThumbnailCanFinish()
    {
        var cache = new DecodedImageCache<object>();
        var source = new object();
        var decoded = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thumbnail = cache.GetAsync(source, _ => decoded.Task);
        using var cancellation = new CancellationTokenSource();
        var viewer = cache.GetAsync(source, _ => throw new InvalidOperationException("Unexpected decode."), cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewer);
        decoded.SetResult(new object());

        Assert.Same(await thumbnail, await cache.GetAsync(source, _ => throw new InvalidOperationException("Unexpected decode.")));
    }

    [Fact]
    public async Task GetAsync_WhenDecodeFails_NextRequestCanDecode()
    {
        var cache = new DecodedImageCache<object>();
        var source = new object();
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(source, _ => throw new IOException()));

        var image = new object();
        Assert.Same(image, await cache.GetAsync(source, _ => Task.FromResult<object?>(image)));
    }

    [Fact]
    public async Task GetAsync_WhenNoViewOwnsBitmap_DoesNotRetainDecodedMemory()
    {
        var cache = new DecodedImageCache<object>();
        var source = new object();
        var weakImage = DecodeWithoutKeepingImage(cache, source);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakImage.TryGetTarget(out _));
        var replacement = new object();
        Assert.Same(replacement, await cache.GetAsync(source, _ => Task.FromResult<object?>(replacement)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> DecodeWithoutKeepingImage(DecodedImageCache<object> cache, object source) =>
        new(cache.GetAsync(source, _ => Task.FromResult<object?>(new object())).GetAwaiter().GetResult()!);
}
