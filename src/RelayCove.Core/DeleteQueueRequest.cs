namespace RelayCove.Core;

public sealed class DeleteQueueRequest
{
    public DeleteQueueRequest(CredentialEnvelope credentials, string queueId)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        Credentials = credentials;
        QueueId = queueId;
    }

    public CredentialEnvelope Credentials { get; }
    public string QueueId { get; }

    public override string ToString() =>
        "DeleteQueueRequest { Credentials = [redacted], QueueId = [redacted] }";
}
