using System.Windows.Media.Imaging;

namespace RelayCove.Client.Attachments;

internal sealed record ClientClipboardImageReadOutcome(
    ClientClipboardImageReadStatus Status,
    BitmapSource? Image)
{
    public override string ToString() =>
        $"{nameof(ClientClipboardImageReadOutcome)} {{ Status = {Status}, Image = [REDACTED] }}";
}
