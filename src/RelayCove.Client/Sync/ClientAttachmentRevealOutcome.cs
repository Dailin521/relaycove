namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentRevealOutcome(ClientAttachmentRevealStatus Status)
{
    internal static ClientAttachmentRevealOutcome FromStatus(
        ClientAttachmentRevealStatus status) =>
        new(status);

    public override string ToString() =>
        $"{nameof(ClientAttachmentRevealOutcome)} {{ Status = {Status} }}";
}
