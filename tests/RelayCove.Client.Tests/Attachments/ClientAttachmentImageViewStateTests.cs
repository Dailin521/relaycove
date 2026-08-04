using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Attachments;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentImageViewStateTests
{
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MessageClientId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AttachmentId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Constructor_WhenEligible_ExposesPreviewPlaceholderAndSafeViewAction()
    {
        var state = CreateState(eligible: true);

        Assert.True(state.IsEligible);
        Assert.True(state.ShowPreview);
        Assert.Null(state.Thumbnail);
        Assert.False(state.IsLoading);
        Assert.False(state.CanView);
        Assert.Equal("图片缩略图待加载。", state.StatusText);
        Assert.Equal("图片预览待加载：safe-image.png", state.AutomationName);
    }

    [Fact]
    public void SynchronizeEligibility_WhenAccessIsRemoved_ClearsImageAndHidesPreview()
    {
        var state = CreateState(eligible: true);
        Assert.True(state.TryBeginLoad());
        Assert.True(state.TryApplyLoaded(CreateFrozenBitmap()));

        state.SynchronizeEligibility(eligible: false);

        Assert.False(state.IsEligible);
        Assert.False(state.ShowPreview);
        Assert.Null(state.Thumbnail);
        Assert.False(state.IsLoading);
        Assert.False(state.CanView);
        Assert.Equal("图片预览不可用。", state.StatusText);
        Assert.Equal("图片预览不可用：safe-image.png", state.AutomationName);
    }

    [Fact]
    public void TryBeginLoad_WhenEligible_StartsOnceAndNotifiesBindings()
    {
        var state = CreateState(eligible: true);
        var changed = new List<string?>();
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        var started = state.TryBeginLoad();

        Assert.True(started);
        Assert.True(state.IsLoading);
        Assert.Equal("正在加载图片缩略图…", state.StatusText);
        Assert.False(state.CanView);
        Assert.Contains(nameof(state.IsLoading), changed);
        Assert.Contains(nameof(state.CanView), changed);
        Assert.Contains(nameof(state.AutomationName), changed);
        Assert.Contains(nameof(state.StatusText), changed);
        Assert.False(state.TryBeginLoad());
    }

    [Fact]
    public void TryApplyLoaded_WhenFrozenAndLoading_ExposesViewAction()
    {
        var state = CreateState(eligible: true);
        var image = CreateFrozenBitmap();
        Assert.True(state.TryBeginLoad());

        var applied = state.TryApplyLoaded(image);

        Assert.True(applied);
        Assert.Same(image, state.Thumbnail);
        Assert.True(state.Thumbnail!.IsFrozen);
        Assert.False(state.IsLoading);
        Assert.True(state.CanView);
        Assert.Equal("图片缩略图已加载。", state.StatusText);
        Assert.Equal("查看图片：safe-image.png", state.AutomationName);
    }

    [Fact]
    public void TryApplyLoaded_WhenImageIsNotFrozen_RejectsAndKeepsLoading()
    {
        var state = CreateState(eligible: true);
        Assert.True(state.TryBeginLoad());
        var unfrozenImage = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

        var applied = state.TryApplyLoaded(unfrozenImage);

        Assert.False(applied);
        Assert.Null(state.Thumbnail);
        Assert.True(state.IsLoading);
        Assert.False(state.CanView);
    }

    [Fact]
    public void TryApplyFailure_WhenLoading_StopsLoadingWithoutExposingImage()
    {
        var state = CreateState(eligible: true);
        Assert.True(state.TryBeginLoad());

        var applied = state.TryApplyFailure("图片内容无法安全预览。");

        Assert.True(applied);
        Assert.Null(state.Thumbnail);
        Assert.False(state.IsLoading);
        Assert.False(state.CanView);
        Assert.Equal("图片内容无法安全预览。", state.StatusText);
    }

    [Fact]
    public void ClearForRecycle_WhenImageWasLoaded_ReleasesStrongReferenceAndRestoresPlaceholder()
    {
        var state = CreateState(eligible: true);
        Assert.True(state.TryBeginLoad());
        Assert.True(state.TryApplyLoaded(CreateFrozenBitmap()));

        state.ClearForRecycle();

        Assert.Null(state.Thumbnail);
        Assert.False(state.IsLoading);
        Assert.False(state.CanView);
        Assert.Equal("图片缩略图待加载。", state.StatusText);
    }

    [Fact]
    public void ToString_WhenCalled_RedactsContextAndDisplayName()
    {
        var state = CreateState(eligible: true);
        var text = string.Join(" | ", state.Context, state);

        Assert.DoesNotContain(ConversationId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MessageClientId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AttachmentId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe-image.png", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    private static ClientAttachmentImageViewState CreateState(bool eligible) =>
        new(
            new ClientAttachmentDownloadContext(
                ConversationId,
                MessageClientId,
                AttachmentId,
                contextVersion: 7),
            "safe-image.png",
            eligible);

    private static BitmapSource CreateFrozenBitmap()
    {
        var image = BitmapSource.Create(
            pixelWidth: 1,
            pixelHeight: 1,
            dpiX: 96,
            dpiY: 96,
            pixelFormat: PixelFormats.Bgra32,
            palette: null,
            pixels: new byte[] { 0, 0, 0, 255 },
            stride: 4);
        image.Freeze();
        return image;
    }
}
