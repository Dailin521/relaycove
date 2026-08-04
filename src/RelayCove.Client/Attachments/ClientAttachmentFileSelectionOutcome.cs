namespace RelayCove.Client.Attachments;

internal sealed record ClientAttachmentFileSelectionOutcome(
    ClientAttachmentFileSelectionStatus Status,
    IReadOnlyList<ClientAttachmentFileSelection> Selections)
{
    private static readonly IReadOnlyList<ClientAttachmentFileSelection> NoSelections =
        Array.Empty<ClientAttachmentFileSelection>();

    public static ClientAttachmentFileSelectionOutcome Success(
        IReadOnlyList<ClientAttachmentFileSelection> selections) =>
        new(ClientAttachmentFileSelectionStatus.Success, selections);

    public static ClientAttachmentFileSelectionOutcome Failure(
        ClientAttachmentFileSelectionStatus status) =>
        new(status, NoSelections);

    public override string ToString() =>
        $"{nameof(ClientAttachmentFileSelectionOutcome)} {{ Status = {Status}, " +
        "Selections = [REDACTED] }";
}
