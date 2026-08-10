namespace RelayCove.Core;

public sealed class GetEventsRequest
{
    public GetEventsRequest(
        CredentialEnvelope credentials,
        string queueId,
        long lastEventId,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        if (lastEventId < -1) throw new ArgumentOutOfRangeException(nameof(lastEventId));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Credentials = credentials;
        QueueId = queueId;
        LastEventId = lastEventId;
        Timeout = timeout;
    }

    public CredentialEnvelope Credentials { get; }
    public string QueueId { get; }
    public long LastEventId { get; }
    public TimeSpan Timeout { get; }

    public override string ToString() =>
        "GetEventsRequest { Credentials = [redacted], QueueId = [redacted], LastEventId = [redacted], Timeout = [redacted] }";
}
