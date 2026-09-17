using RelayCove.App.Controls;
using RelayCove.App.Services;
using RelayCove.Core;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RelayCove.Preview.NativeTests;

internal sealed class FixtureMediaService : IRealmMediaService
{
    internal static int DelayedCancellations { get; private set; }
    public event EventHandler<AvatarChangedEventArgs>? AvatarChanged { add { } remove { } }
    internal static void GenerateFixtures()
    {
        foreach (var (name, width, height) in new[] { ("image.png", 320, 200), ("image.jpg", 440, 250), ("large.png", 4096, 3072) })
        {
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.CornflowerBlue);
            graphics.FillEllipse(System.Drawing.Brushes.Orange, width / 4, height / 4, width / 2, height / 2);
            bitmap.Save(Path.Combine(ProbeLog.DirectoryPath, name), name.EndsWith("jpg") ? ImageFormat.Jpeg : ImageFormat.Png);
        }
        // Two valid transparent 1x1 GIF frames, each with a 100ms delay.
        var gif = Convert.FromHexString("47494638396101000100800000000000FFFFFF21FF0B4E45545343415045322E30030100000021F904010A0000002C000000000100010000020244010021F904010A0000002C00000000010001000002024401003B");
        File.WriteAllBytes(Path.Combine(ProbeLog.DirectoryPath, "transparent.gif"), gif);
    }
    public async Task<ImageSource> GetImageAsync(string sourceUrl, RealmMediaKind kind, CancellationToken cancellationToken = default)
    {
        ProbeLog.Write("media-start", sourceUrl);
        if (sourceUrl == "delayed")
        {
            try { await Task.Delay(1500, cancellationToken); }
            catch (OperationCanceledException) { DelayedCancellations++; ProbeLog.Write("delayed-canceled"); throw; }
            sourceUrl = "image.png";
        }
        if (sourceUrl is not ("image.png" or "image.jpg" or "large.png" or "transparent.gif")) throw new InvalidOperationException("Only generated local fixtures are allowed.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(ProbeLog.DirectoryPath, sourceUrl), cancellationToken);
        ProbeLog.Write("media-ready", sourceUrl);
        if (sourceUrl.EndsWith(".gif", StringComparison.Ordinal))
            return (ImageSource)Activator.CreateInstance(typeof(RealmMediaImageView).Assembly.GetType("RelayCove.App.Platforms.Windows.RealmGifImageSource", true)!, bytes)!;
        var source = (StreamImageSource)Activator.CreateInstance(typeof(RealmMediaImageView).Assembly.GetType("RelayCove.App.Platforms.Windows.RealmImageSource", true)!, nonPublic: true)!;
        source.Stream = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        return source;
    }
    public Task<RealmMediaResult> GetFileAsync(string sourceUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealmMediaDownloadResult> DownloadFileAsync(string sourceUrl, Stream destination, IProgress<RealmMediaTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
