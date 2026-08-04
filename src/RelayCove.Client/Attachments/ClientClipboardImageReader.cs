using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace RelayCove.Client.Attachments;

internal static class ClientClipboardImageReader
{
    public static bool IsExactImagePasteGesture(Key key, ModifierKeys modifiers) =>
        key == Key.V && modifiers == ModifierKeys.Control;

    public static ClientClipboardImageReadOutcome TryRead(
        bool suppressRepeatedImageRead,
        Func<bool> containsText,
        Func<bool> containsImage,
        Func<BitmapSource?> readImage)
    {
        ArgumentNullException.ThrowIfNull(containsText);
        ArgumentNullException.ThrowIfNull(containsImage);
        ArgumentNullException.ThrowIfNull(readImage);
        try
        {
            if (containsText())
            {
                return new ClientClipboardImageReadOutcome(
                    ClientClipboardImageReadStatus.TextPreferred,
                    Image: null);
            }

            if (!containsImage())
            {
                return new ClientClipboardImageReadOutcome(
                    ClientClipboardImageReadStatus.NoImage,
                    Image: null);
            }

            if (suppressRepeatedImageRead)
            {
                return new ClientClipboardImageReadOutcome(
                    ClientClipboardImageReadStatus.RepeatedImagePaste,
                    Image: null);
            }

            var image = readImage();
            return image is null
                ? new ClientClipboardImageReadOutcome(
                    ClientClipboardImageReadStatus.InvalidImage,
                    Image: null)
                : new ClientClipboardImageReadOutcome(
                    ClientClipboardImageReadStatus.Success,
                    image);
        }
        catch (ExternalException)
        {
            return new ClientClipboardImageReadOutcome(
                ClientClipboardImageReadStatus.ClipboardUnavailable,
                Image: null);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return new ClientClipboardImageReadOutcome(
                ClientClipboardImageReadStatus.InvalidImage,
                Image: null);
        }
    }

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
