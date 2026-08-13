namespace RelayCove.Core;

public sealed class EditMessageRequest
{
    public EditMessageRequest(CredentialEnvelope credentials, long messageId, string content, string previousContentSha256)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousContentSha256);
        Credentials = credentials;
        MessageId = messageId;
        Content = content;
        PreviousContentSha256 = previousContentSha256;
    }

    public CredentialEnvelope Credentials { get; }
    public long MessageId { get; }
    public string Content { get; }
    public string PreviousContentSha256 { get; }

    public override string ToString() =>
        $"EditMessageRequest {{ Credentials = [redacted], MessageId = {MessageId}, Content = [redacted], PreviousContentSha256 = [redacted] }}";
}
