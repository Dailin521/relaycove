namespace RelayCove.Core;

public sealed class MarkReadRequest
{
    public MarkReadRequest(
        CredentialEnvelope credentials,
        IReadOnlyCollection<long> messageIds)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count is < 1 or > 50 || messageIds.Any(static id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(messageIds));
        }

        Credentials = credentials;
        MessageIds = Array.AsReadOnly(messageIds.Distinct().ToArray());
    }

    public CredentialEnvelope Credentials { get; }
    public IReadOnlyList<long> MessageIds { get; }

    public override string ToString() =>
        $"MarkReadRequest {{ Credentials = [redacted], MessageIds = [redacted], Count = {MessageIds.Count} }}";
}
