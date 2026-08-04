namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentOpenOutcome(ClientAttachmentOpenStatus Status)
{
    internal static ClientAttachmentOpenOutcome FromStatus(ClientAttachmentOpenStatus status) =>
        new(status);

    public override string ToString() =>
        $"{nameof(ClientAttachmentOpenOutcome)} {{ Status = {Status} }}";
}
