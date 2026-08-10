namespace RelayCove.Core;

public sealed class TopicsRequest
{
    public TopicsRequest(CredentialEnvelope credentials, long channelId)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        Credentials = credentials;
        ChannelId = channelId;
    }

    public CredentialEnvelope Credentials { get; }
    public long ChannelId { get; }

    public override string ToString() =>
        "TopicsRequest { Credentials = [redacted], ChannelId = [redacted] }";
}
