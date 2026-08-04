namespace RelayCove.Client.Attachments;

internal sealed record ClientAttachmentClipboardImageSelectionOutcome(
    ClientAttachmentClipboardImageSelectionStatus Status,
    ClientAttachmentDraft? Selection)
{
    public static ClientAttachmentClipboardImageSelectionOutcome Success(
        ClientAttachmentDraft selection) =>
        new(ClientAttachmentClipboardImageSelectionStatus.Success, selection);

    public static ClientAttachmentClipboardImageSelectionOutcome Failure(
        ClientAttachmentClipboardImageSelectionStatus status) =>
        new(status, null);

    public override string ToString() =>
        $"{nameof(ClientAttachmentClipboardImageSelectionOutcome)} {{ Status = {Status}, " +
        "Selection = [REDACTED] }";
}
