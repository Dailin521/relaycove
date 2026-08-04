namespace RelayCove.Client.Storage;

internal sealed record LocalAttachmentReservationOutcome(
    LocalCacheOperationStatus Status,
    LocalAttachmentReservationResult? Result)
{
    public static LocalAttachmentReservationOutcome Failure(LocalCacheOperationStatus status) =>
        new(status, Result: null);

    public override string ToString() =>
        $"{nameof(LocalAttachmentReservationOutcome)} {{ Status = {Status}, Result = {Result} }}";
}
