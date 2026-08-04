using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelayCove.Client.Attachments;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientClipboardImageReaderTests
{
    [Theory]
    [InlineData(Key.V, ModifierKeys.Control, true)]
    [InlineData(Key.V, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData(Key.V, ModifierKeys.Control | ModifierKeys.Alt, false)]
    [InlineData(Key.V, ModifierKeys.None, false)]
    [InlineData(Key.C, ModifierKeys.Control, false)]
    public void IsExactImagePasteGesture_WhenKeyOrModifiersDiffer_ReturnsExpected(
        Key key,
        ModifierKeys modifiers,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClientClipboardImageReader.IsExactImagePasteGesture(key, modifiers));
    }

    [Fact]
    public void TryRead_WhenClipboardHasNoImage_DoesNotReadPayload()
    {
        var readCount = 0;

        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => false,
            containsImage: () => false,
            readImage: () =>
            {
                Interlocked.Increment(ref readCount);
                return CreateImage();
            });

        Assert.Equal(ClientClipboardImageReadStatus.NoImage, outcome.Status);
        Assert.Null(outcome.Image);
        Assert.Equal(0, Volatile.Read(ref readCount));
    }

    [Fact]
    public void TryRead_WhenClipboardReturnsImage_PreservesReference()
    {
        var image = CreateImage();

        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => false,
            containsImage: () => true,
            readImage: () => image);

        Assert.Equal(ClientClipboardImageReadStatus.Success, outcome.Status);
        Assert.Same(image, outcome.Image);
    }

    [Fact]
    public void TryRead_WhenClipboardReturnsNull_ClassifiesInvalidImage()
    {
        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => false,
            containsImage: () => true,
            readImage: () => null);

        Assert.Equal(ClientClipboardImageReadStatus.InvalidImage, outcome.Status);
        Assert.Null(outcome.Image);
    }

    [Fact]
    public void TryRead_WhenClipboardIsBusy_ClassifiesUnavailable()
    {
        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => false,
            containsImage: () => throw new ExternalException("clipboard busy"),
            readImage: () => CreateImage());

        Assert.Equal(ClientClipboardImageReadStatus.ClipboardUnavailable, outcome.Status);
        Assert.Null(outcome.Image);
    }

    [Fact]
    public void TryRead_WhenUnexpectedNoncriticalFailureOccurs_ClassifiesInvalidImage()
    {
        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => false,
            containsImage: () => true,
            readImage: () => throw new InvalidOperationException("unexpected"));

        Assert.Equal(ClientClipboardImageReadStatus.InvalidImage, outcome.Status);
        Assert.Null(outcome.Image);
    }

    [Fact]
    public void TryRead_WhenTextAndImageAreAvailable_PrefersTextWithoutReadingImage()
    {
        var imageProbeCount = 0;

        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: false,
            containsText: () => true,
            containsImage: () =>
            {
                Interlocked.Increment(ref imageProbeCount);
                return true;
            },
            readImage: CreateImage);

        Assert.Equal(ClientClipboardImageReadStatus.TextPreferred, outcome.Status);
        Assert.Null(outcome.Image);
        Assert.Equal(0, Volatile.Read(ref imageProbeCount));
    }

    [Fact]
    public void TryRead_WhenImageKeyRepeats_SuppressesRepeatWithoutReadingPayload()
    {
        var readCount = 0;

        var outcome = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: true,
            containsText: () => false,
            containsImage: () => true,
            readImage: () =>
            {
                Interlocked.Increment(ref readCount);
                return CreateImage();
            });

        Assert.Equal(ClientClipboardImageReadStatus.RepeatedImagePaste, outcome.Status);
        Assert.Null(outcome.Image);
        Assert.Equal(0, Volatile.Read(ref readCount));
    }

    [Fact]
    public void ToString_WhenImageWasRead_RedactsImageDetails()
    {
        var image = CreateImage();
        var outcome = new ClientClipboardImageReadOutcome(
            ClientClipboardImageReadStatus.Success,
            image);

        var text = outcome.ToString();

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(image.PixelWidth.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(image.PixelHeight.ToString(), text, StringComparison.Ordinal);
    }

    private static BitmapSource CreateImage() => BitmapSource.Create(
        pixelWidth: 2,
        pixelHeight: 2,
        dpiX: 96,
        dpiY: 96,
        PixelFormats.Bgra32,
        palette: null,
        pixels: new byte[16],
        stride: 8);
}
