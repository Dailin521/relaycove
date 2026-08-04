namespace RelayCove.Client.Attachments;

internal sealed record ClientAttachmentFileSelectionOutcome(
    ClientAttachmentFileSelectionStatus Status,
    IReadOnlyList<ClientAttachmentDraft> Selections)
{
    private static readonly IReadOnlyList<ClientAttachmentDraft> NoSelections =
        Array.Empty<ClientAttachmentDraft>();

    public static ClientAttachmentFileSelectionOutcome Success(
        IReadOnlyList<ClientAttachmentDraft> selections) =>
        new(ClientAttachmentFileSelectionStatus.Success, selections);

    public static ClientAttachmentFileSelectionOutcome Failure(
        ClientAttachmentFileSelectionStatus status) =>
        new(status, NoSelections);

    public override string ToString() =>
        $"{nameof(ClientAttachmentFileSelectionOutcome)} {{ Status = {Status}, " +
        "Selections = [REDACTED] }";
}
