namespace RelayCove.Core;

public sealed class SendRequest
{
    public SendRequest(
        CredentialEnvelope credentials,
        string queueId,
        string localId,
        ConversationKey conversation,
        string content)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Credentials = credentials;
        QueueId = queueId;
        LocalId = localId;
        Conversation = conversation;
        Content = content;
    }

    public CredentialEnvelope Credentials { get; }
    public string QueueId { get; }
    public string LocalId { get; }
    public ConversationKey Conversation { get; }
    public string Content { get; }

    public override string ToString() =>
        "SendRequest { Credentials = [redacted], QueueId = [redacted], LocalId = [redacted], Conversation = [redacted], Content = [redacted] }";
}
